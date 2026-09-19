using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Haley.Internal.KeyConstants;

namespace Haley.Services {
    // BlueprintImporter is the "write path" for definitions and policies.
    // It parses JSON, computes a SHA-256 hash of the content, and writes to the DB atomically.
    // Hash-guarding makes it fully idempotent: importing the same JSON twice is a no-op that
    // returns the existing ID. Call both methods every application startup - they're always safe.
    //
    // Definition import creates: environment, definition, def_version, categories, events, states, transitions.
    // Policy import creates: policy (hash -> content), timeouts, and attaches the policy to the definition.
    internal sealed class BlueprintImporter : IBlueprintImporter {
        private readonly IWorkFlowDAL _dal;
        public BlueprintImporter(IWorkFlowDAL dal) { _dal = dal ?? throw new ArgumentNullException(nameof(dal)); }

        public async Task<long> ImportDefinitionJsonAsync(int envCode, string envDisplayName, string definitionJson, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(definitionJson)) throw new ArgumentNullException(nameof(definitionJson));
            ValidateProtocol(definitionJson, null, envCode);

            using var doc = JsonDocument.Parse(definitionJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Definition JSON root must be an object.");
            if (root.TryGetProperty(KEY_DEFINITION, out _)) throw new InvalidOperationException("Legacy definition wrapper is not supported. Move definition fields to the top level.");

            var defName = root.GetString(KEY_NAME) ?? throw new InvalidOperationException("Definition JSON must contain top-level name.");
            var defDesc = root.GetString(KEY_DESCRIPTION);
            var requestedVer = root.GetInt(KEY_VERSION) ?? 0;

            var transaction = _dal.CreateNewTransaction();
            using var tx = transaction.Begin(false);
            var load = new DbExecutionLoad(ct, transaction);
            var committed = false;

            try {
                var envId = await _dal.BlueprintWrite.EnsureEnvironmentByCodeAsync(envCode, envDisplayName, load);
                var defId = await _dal.BlueprintWrite.EnsureDefinitionByEnvIdAsync(envId, defName, defDesc, load);
                var attachedPolicy = await _dal.Blueprint.GetPolicyForDefinition(defId, load);
                ValidateProtocol(definitionJson, attachedPolicy?.GetString(KEY_CONTENT), envCode);

                var defhashMaterial = root.BuildDefinitionHashMaterial();
                var defHash = defhashMaterial.CreateGUID(HashMethod.Sha256).ToString();
                var existing = await _dal.Blueprint.GetDefVersionByParentAndHashAsync(defId, defHash, load);
                if (existing != null) {
                    tx.Commit();
                    committed = true;
                    return existing.GetLong(KEY_ID);
                }

                var nextVer = await _dal.Blueprint.GetNextDefVersionNumberByEnvCodeAndDefNameAsync(envCode, defName, load) ?? 1;
                var verToUse = requestedVer > 0 ? requestedVer : nextVer;
                if (requestedVer > 0 && requestedVer < nextVer) throw new InvalidOperationException($"JSON version={requestedVer} but DB next_version={nextVer}. Import rejected. Requested version should either be equal to or greater than the next available version. Leave version empty in the json to automatically assign the version.");

                var defVersionId = await _dal.BlueprintWrite.InsertDefVersionAsync(defId, verToUse, defhashMaterial, defHash, load);

                var categoryMap = await ImportCategoriesFromStatesAsync(root, load);
                var eventsByCode = await ImportEventsFromTransitionsAsync(defVersionId, root, load);
                var statesByName = await ImportStatesAsync(defVersionId, root, categoryMap, load);
                await ImportTransitionsAsync(defVersionId, root, statesByName, eventsByCode, load);

                tx.Commit();
                committed = true;
                return defVersionId;
            } catch {
                if (!committed) tx.Rollback();
                throw;
            }
        }

        async Task ImportEmitRoutesAsync(JsonElement root, DbExecutionLoad load) {
            if (!root.TryGetProperty(KEY_RULES, out var rules) || rules.ValueKind != JsonValueKind.Array) return;

            foreach (var rule in rules.EnumerateArray()) {
                if (!rule.TryGetProperty(KEY_EMIT, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                foreach (var e in arr.EnumerateArray()) {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var routeName = e.GetString(KEY_ROUTE);
                    if (string.IsNullOrWhiteSpace(routeName)) continue;
                    var label = e.GetString(KEY_LABEL) ?? string.Empty;
                    await _dal.BlueprintWrite.UpsertHookRouteLabelAsync(routeName, label, load);
                }
            }
        }

        async Task ImportPolicyTimeoutsAsync(long policyId, JsonElement root, DbExecutionLoad load) {
            await _dal.BlueprintWrite.DeleteByPolicyIdAsync(policyId, load);

            if (!root.TryGetProperty(KEY_TIMEOUTS, out var arr) || arr.ValueKind != JsonValueKind.Array) return;

            foreach (var t in arr.EnumerateArray()) {
                if (t.ValueKind != JsonValueKind.Object) continue;

                var stateRaw = t.TryGetProperty(KEY_STATE, out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (string.IsNullOrWhiteSpace(stateRaw)) continue;

                var stateName = stateRaw.Normalize(true);
                var durationMinutes = ParseTimeoutMinutes(t) ?? 0;
                var mode = ParseTimeoutMode(t);
                var eventCode = ParseTimeoutEventCode(t);
                var maxRetry = ParseMaxRetry(t);

                if (durationMinutes <= 0) throw new ArgumentException($"Invalid timeout duration for state '{stateRaw}'.");

                await _dal.BlueprintWrite.InsertAsync(policyId, stateName, durationMinutes, mode, eventCode, maxRetry, load);
            }
        }

        public async Task<long> ImportPolicyJsonAsync(int envCode, string envDisplayName, string policyJson, CancellationToken ct = default) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(policyJson)) throw new ArgumentNullException(nameof(policyJson));

            using var doc = JsonDocument.Parse(policyJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Policy JSON root must be an object.");

            var defName = root.GetString(KEY_DEF_NAME_CAMEL);
            if (string.IsNullOrWhiteSpace(defName)) throw new InvalidOperationException("Policy JSON must contain top-level defName.");

            var transaction = _dal.CreateNewTransaction();
            using var tx = transaction.Begin(false);
            var load = new DbExecutionLoad(ct, transaction);
            var committed = false;
            try {
                var definition = await _dal.Blueprint.GetLatestDefVersionByEnvCodeAndDefNameAsync(envCode, defName!, load)
                    ?? throw new InvalidOperationException("Import the definition before its policy.");
                var definitionDocument = JsonNode.Parse(definition.GetString(KEY_DATA) ?? "{}")!.AsObject();
                definitionDocument[KEY_NAME] = defName;
                ValidateProtocol(definitionDocument.ToJsonString(), policyJson, envCode);

                var policyHashmaterial = root.BuildPolicyHashMaterial();
                var hash = policyHashmaterial.CreateGUID(HashMethod.Sha256).ToString();
                var policyId = await _dal.BlueprintWrite.EnsurePolicyByHashAsync(hash, policyHashmaterial, load);

                await ImportPolicyTimeoutsAsync(policyId, root, load);
                await ImportEmitRoutesAsync(root, load);
                await _dal.BlueprintWrite.AttachPolicyToDefinitionByEnvCodeAndDefNameAsync(envCode, defName!, policyId, load);

                tx.Commit();
                committed = true;
                return policyId;
            } catch {
                if (!committed) tx.Rollback();
                throw;
            }
        }

        private static void ValidateProtocol(string definitionJson, string? policyJson, int envCode) {
            var snapshot = DefinitionJsonReader.ReadSnapshot(definitionJson, policyJson, envCode);
            var validation = PolicyValidator.Validate(snapshot);
            var errors = validation.Findings.Where(f => f.Severity == PolicyFindingSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("Protocol validation failed: " + string.Join("; ", errors.Select(f => f.Code + ": " + f.Message)));
        }

        private async Task<Dictionary<string, int>> ImportCategoriesFromStatesAsync(JsonElement root, DbExecutionLoad load) {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty(KEY_STATES, out var states) || states.ValueKind != JsonValueKind.Array) return map;

            foreach (var s in states.EnumerateArray()) {
                var cat = s.GetString(KEY_CATEGORY);
                if (string.IsNullOrWhiteSpace(cat)) cat = "default";

                var key = cat.N();
                if (map.ContainsKey(key)) continue;

                var id = await _dal.BlueprintWrite.EnsureCategoryByNameAsync(cat!, load);
                map[key] = id;
            }

            return map;
        }

        private async Task<Dictionary<int, EventDef>> ImportEventsFromTransitionsAsync(long defVersionId, JsonElement root, DbExecutionLoad load) {
            var byCode = new Dictionary<int, EventDef>();
            if (!root.TryGetProperty(KEY_TRANSITIONS, out var transitions) || transitions.ValueKind != JsonValueKind.Array) return byCode;

            foreach (var t in transitions.EnumerateArray()) {
                if (t.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Each transition must be an object.");

                var code = t.GetInt(KEY_EVENT) ?? throw new InvalidOperationException("Each transition must contain integer event.");
                if (byCode.ContainsKey(code)) continue;

                var name = t.GetString(KEY_EVENT_NAME);
                if (string.IsNullOrWhiteSpace(name)) name = $"event_{code}";

                var id = await _dal.BlueprintWrite.InsertEventAsync(defVersionId, name!, code, load);
                byCode[code] = new EventDef { Id = id, Code = code, Name = name!.N(), DisplayName = name! };
            }

            return byCode;
        }

        private async Task<Dictionary<string, StateDef>> ImportStatesAsync(long defVersionId, JsonElement root, Dictionary<string, int> categoryMap, DbExecutionLoad load) {
            var map = new Dictionary<string, StateDef>(StringComparer.OrdinalIgnoreCase);
            var snapshotStates = DefinitionJsonReader.ReadSnapshot(root.GetRawText()).States;
            if (!root.TryGetProperty(KEY_STATES, out var states) || states.ValueKind != JsonValueKind.Array) return map;

            foreach (var s in states.EnumerateArray()) {
                if (s.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Each state must be an object.");

                var name = s.GetString(KEY_NAME) ?? throw new InvalidOperationException("Each state must contain name.");
                var key = name.N();
                if (map.ContainsKey(key)) throw new InvalidOperationException($"Duplicate state name in JSON: {name}");

                var flags = (uint)(s.GetInt(KEY_FLAGS) ?? 0);
                if (s.GetBool(KEY_IS_INITIAL) == true) flags |= (uint)LifeCycleStateFlag.IsInitial;
                if (s.GetBool(KEY_IS_FINAL) == true) flags |= (uint)LifeCycleStateFlag.IsFinal;
                if (snapshotStates.Single(st => string.Equals(st.Name, name, StringComparison.OrdinalIgnoreCase)).IsTerminal)
                    flags |= (uint)LifeCycleStateFlag.IsFinal;

                var catName = s.GetString(KEY_CATEGORY);
                if (string.IsNullOrWhiteSpace(catName)) catName = "default";
                var catId = (!string.IsNullOrWhiteSpace(catName) && categoryMap.TryGetValue(catName!.N(), out var cid)) ? cid : 0;

                var id = await _dal.BlueprintWrite.InsertStateAsync(defVersionId, catId, name, flags, load);

                map[key] = new StateDef {
                    Id = id,
                    Name = key,
                    DisplayName = name,
                    Flags = flags,
                    IsInitial = (flags & (uint)LifeCycleStateFlag.IsInitial) != 0
                };
            }

            return map;
        }

        private async Task ImportTransitionsAsync(long defVersionId, JsonElement root, Dictionary<string, StateDef> statesByName, Dictionary<int, EventDef> eventsByCode, DbExecutionLoad load) {
            if (!root.TryGetProperty(KEY_TRANSITIONS, out var trans) || trans.ValueKind != JsonValueKind.Array) return;

            foreach (var t in trans.EnumerateArray()) {
                if (t.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Each transition must be an object.");

                var fromName = t.GetString(KEY_FROM) ?? throw new InvalidOperationException("Each transition must contain from.");
                var toName = t.GetString(KEY_TO) ?? throw new InvalidOperationException("Each transition must contain to.");
                var evCode = t.GetInt(KEY_EVENT) ?? throw new InvalidOperationException("Each transition must contain integer event.");

                if (!statesByName.TryGetValue(fromName.N(), out var from)) throw new InvalidOperationException($"Transition from-state not found: {fromName}");
                if (!statesByName.TryGetValue(toName.N(), out var to)) throw new InvalidOperationException($"Transition to-state not found: {toName}");
                if (!eventsByCode.TryGetValue(evCode, out var ev)) throw new InvalidOperationException($"Transition event code not found: {evCode}");

                await _dal.BlueprintWrite.InsertTransitionAsync(defVersionId, from.Id, to.Id, ev.Id, load);
            }
        }

        static int? ParseTimeoutMinutes(JsonElement stateNode) {
            var tm = stateNode.GetInt(KEY_TIMEOUT_MINUTES);
            if (tm.HasValue) return tm;

            var dur = stateNode.GetString(KEY_TIMEOUT);
            if (string.IsNullOrWhiteSpace(dur)) return null;
            if (!ISODurationUtils.TryToMinutes(dur!, out var mins) || mins <= 0) return null;
            return mins;
        }

        static int ParseTimeoutMode(JsonElement stateNode) {
            var n = stateNode.GetInt(KEY_TIMEOUT_MODE);
            if (n.HasValue) return n.Value;

            var s = stateNode.GetString(KEY_TIMEOUT_MODE);
            if (string.IsNullOrWhiteSpace(s)) return 0;
            return string.Equals(s.Trim(), "repeat", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        static int? ParseTimeoutEventCode(JsonElement t) {
            if (!t.TryGetProperty(KEY_TIMEOUT_EVENT, out var e)) return null;
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var c)) return c;
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs)) return cs;
            return null;
        }

        static int? ParseMaxRetry(JsonElement t) {
            var n = t.GetInt(KEY_MAX_RETRY);
            return n > 0 ? n : null;
        }
    }
}
