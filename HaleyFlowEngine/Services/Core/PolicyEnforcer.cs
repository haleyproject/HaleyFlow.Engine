using Haley.Abstractions;
using Haley.Enums;
using Haley.Internal;
using static Haley.Internal.KeyConstants;
using Haley.Models;
using Haley.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Services {

    // PolicyEnforcer reads and evaluates the policy JSON to answer two questions:
    //   1. "Which hooks should fire for this transition, and in which order/phase?" → EmitHooksAsync
    //   2. "What params and completion event codes does the engine need to resolve progression?" → ResolveRuleContextFromJson
    //
    // A policy is a JSON document containing:
    //   - rules: array of { state, via?, blocking?, emit[], params[], complete{} }
    //   - params: catalog of named param sets that rules/emits can reference by code
    //
    // Each rule matches a (targetState, optionalEvent) pair. The emit[] array says which hooks fire.
    // The complete{} block is engine-owned routing metadata for success/failure resolution.
    // The params[] field specifies which param sets from the catalog to include in the event.
    //
    // Policy JSON is parsed ONCE per policyId and stored in _policyCache as a ParsedPolicy.
    // Subsequent calls for the same policy hit only memory — no JSON parsing, no allocations.
    // Call ClearPolicyCache() (wired into WorkFlowEngine.ClearCacheAsync / InvalidateAsync) to
    // force a reload after a policy is re-imported.
    internal sealed class PolicyEnforcer : IPolicyEnforcer {
        private readonly IWorkFlowDAL _dal;
        // Keyed by policyId. Values are immutable ParsedPolicy objects — no lock needed on reads.
        private readonly ConcurrentDictionary<long, ParsedPolicy> _policyCache = new();

        public PolicyEnforcer(IWorkFlowDAL dal) { _dal = dal ?? throw new ArgumentNullException(nameof(dal)); }

        public void ClearPolicyCache() => _policyCache.Clear();

        // Fetches the LATEST policy for a definition. Used when creating a NEW instance —
        // the instance is stamped with this policy_id and will use it for its entire lifetime.
        public async Task<PolicyResolution> ResolvePolicyAsync(LifeCycleBlueprint bp, DbRow instance, ApplyTransitionResult applied, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            var pr = new PolicyResolution();
            if (!applied.Applied) return pr;
            return await ResolvePolicyAsync(bp.DefinitionId, load);
        }

        // Fetches a SPECIFIC policy by ID. Used for EXISTING instances — they must be evaluated
        // against the policy that was active when they were created, not the current latest policy.
        // This is the key isolation guarantee: policy changes don't affect in-flight workflows.
        public async Task<PolicyResolution> ResolvePolicyByIdAsync(long policyId, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return PreparePolicyResolution(await _dal.Blueprint.GetPolicyByIdAsync(policyId, load));
        }

        public async Task<PolicyResolution> ResolvePolicyAsync(long definitionId, DbExecutionLoad load = default) {
            load.Ct.ThrowIfCancellationRequested();
            return PreparePolicyResolution(await _dal.Blueprint.GetPolicyForDefinition(definitionId, load));
        }

        PolicyResolution PreparePolicyResolution(DbRow? pol) {
            var pr = new PolicyResolution();
            if (pol == null) return pr;
            pr.PolicyId = pol.GetNullableLong(KEY_ID);
            pr.PolicyHash = pol.GetString(KEY_HASH);
            pr.PolicyJson = pol.GetString(KEY_CONTENT);
            return pr;
        }

        // Returns a ParsedPolicy for the given json, using the policyId cache when possible.
        // When policyId <= 0 (caller doesn't have an ID), we parse without caching.
        private ParsedPolicy GetOrAddCached(long policyId, string json) {
            if (policyId <= 0) return ParseJsonToPolicy(json);
            return _policyCache.GetOrAdd(policyId, _ => ParseJsonToPolicy(json));
        }

        // Builds the fully materialized ParsedPolicy from a raw JSON string.
        // This is the only place where JsonDocument.Parse is called for policy evaluation.
        // All downstream methods work with the resulting C# object graph — no JSON parsing.
        private static ParsedPolicy ParseJsonToPolicy(string json) {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Build param catalog: code → data dictionary.
            // Lives at policy scope; multiple rules can share the same param set by referencing its code.
            var catalog = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty(KEY_PARAMS, out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Array) {
                foreach (var p in paramsEl.EnumerateArray()) {
                    var code = p.GetString(KEY_CODE);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    catalog[code!] = p.TryGetProperty(KEY_DATA, out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
                        ? p.GetDictionary(KEY_DATA) : null;
                }
            }

            // Build rule list. Completion events are collapsed at parse time:
            // emit.OnSuccess/OnFailure already fall back to rule-level values here so callers
            // don't need to carry both and merge at evaluation time.
            // send is parsed here as a runtime hint only; semantic validation stays in the
            // dedicated protocol/policy validation layer.
            var rules = new List<ParsedPolicyRule>();
            if (root.TryGetProperty(KEY_RULES, out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array) {
                foreach (var ruleEl in rulesEl.EnumerateArray()) {
                    var ruleState = ruleEl.GetString(KEY_STATE);
                    if (string.IsNullOrWhiteSpace(ruleState)) continue;

                    int? via = null;
                    if (ruleEl.TryGetProperty(KEY_VIA, out var viaEl) && viaEl.ValueKind != JsonValueKind.Null && viaEl.ValueKind != JsonValueKind.Undefined)
                        via = viaEl.GetIntValue();

                    var (ruleSuccess, ruleFailure) = ReadCompletionEvents(ruleEl);
                    var ruleParamCodes = ruleEl.ReadList(KEY_PARAMS);
                    var ruleType = ReadHookType(ruleEl);

                    var emits = new List<ParsedPolicyEmit>();
                    if (ruleEl.TryGetProperty(KEY_EMIT, out var emitArr) && emitArr.ValueKind == JsonValueKind.Array) {
                        foreach (var e in emitArr.EnumerateArray()) {
                            var hookRoute = e.GetString(KEY_ROUTE);
                            if (string.IsNullOrWhiteSpace(hookRoute)) continue;

                            var (emitSuccess, emitFailure) = ReadCompletionEvents(e);
                            var orderSeq = e.TryGetProperty(KEY_ORDER, out var orderEl) && orderEl.TryGetInt32(out var oVal) ? oVal : int.MaxValue;
                            var ackModeStr = e.GetString(KEY_ACK_MODE);
                            var groupRaw = e.GetString(KEY_GROUP);
                            var sendStr = e.GetString(KEY_SEND);

                            emits.Add(new ParsedPolicyEmit {
                                Route = hookRoute!,
                                Type = ReadHookType(e),
                                Group = string.IsNullOrWhiteSpace(groupRaw) ? null : groupRaw,
                                OrderSeq = orderSeq,
                                AckMode = string.Equals(ackModeStr, "any", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                                SendAlways = string.Equals(sendStr, "always", StringComparison.OrdinalIgnoreCase),
                                // Collapse completion fallback at parse time: emit wins, else rule's value.
                                OnSuccess = emitSuccess,
                                OnFailure = emitFailure,
                                ParamCodes = e.ReadList(KEY_PARAMS),
                                NotBefore = e.GetDatetimeOffset(KEY_NOT_BEFORE),
                                Deadline = e.GetDatetimeOffset(KEY_DEADLINE)
                            });
                        }
                    }

                    rules.Add(new ParsedPolicyRule {
                        State = ruleState!,
                        Via = via,
                        Type = ruleType,
                        OnSuccess = ruleSuccess,
                        OnFailure = ruleFailure,
                        ParamCodes = ruleParamCodes,
                        Emits = emits
                    });
                }
            }

            return new ParsedPolicy { Rules = rules, ParamCatalog = catalog };
        }

        // This is the hook fan-out decision engine. After a state transition:
        //   1. Get ParsedPolicy from cache (or parse once if not cached). For each rule: check if its
        //      state matches the target state and (if "via" is set) the triggering event matches.
        //   2. For each matched rule's emit entries: collect hook specs — route, blocking, group,
        //      order_seq, ack_mode, on_success/failure event codes, params.
        //   3. Determine minOrder across ALL collected specs.
        //      Only min-order hooks get dispatched=true (ACK rows created, events fired now).
        //      Higher-order hooks get dispatched=false (row created in DB, but no ACK/event yet).
        //      They wait for AdvanceNextHookOrderAsync to activate them after prior-order completion.
        public async Task<IReadOnlyList<ILifeCycleHookEmission>> EmitHooksAsync(LifeCycleBlueprint bp, DbRow instance, ApplyTransitionResult applied, DbExecutionLoad load = default, PolicyResolution? policy = null) {
            load.Ct.ThrowIfCancellationRequested();
            if (!applied.Applied) return Array.Empty<ILifeCycleHookEmission>();

            // Prefer caller-supplied policy (already loaded, has policyId for cache hit).
            // Fall back to a fresh DB fetch if the caller didn't have it (cache by the fetched ID).
            string? policyJson = policy?.PolicyJson;
            long policyId = policy?.PolicyId ?? 0;
            if (string.IsNullOrWhiteSpace(policyJson)) {
                var pol = await _dal.Blueprint.GetPolicyForDefinition(bp.DefinitionId, load);
                policyJson = pol?.GetString(KEY_CONTENT);
                if (policyId <= 0 && pol != null) policyId = pol.GetNullableLong(KEY_ID) ?? 0;
            }
            if (string.IsNullOrWhiteSpace(policyJson)) return Array.Empty<ILifeCycleHookEmission>();

            if (!bp.StatesById.TryGetValue(applied.ToStateId, out var toState)) return Array.Empty<ILifeCycleHookEmission>();
            bp.EventsById.TryGetValue(applied.EventId, out var viaEvent);

            var instanceId = instance.GetLong(KEY_ID);

            // Get or build the parsed policy (cached by policyId — zero cost on repeated calls).
            var parsed = GetOrAddCached(policyId, policyJson!);
            if (parsed.Rules.Count == 0) return Array.Empty<ILifeCycleHookEmission>();

            // Collect all emit specs so we can compute the first dispatchable phase.
            var specs = new List<(string hookCode, HookType hookType, string? emitGroup, int orderSeq, int ackMode, bool sendAlways,
                                  string? onSuccess, string? onFailure,
                                  DateTimeOffset? notBefore, DateTimeOffset? deadline,
                                  IReadOnlyList<LifeCycleParamItem>? resolvedParams)>();

            var selectedRule = SelectBestRule(parsed, toState, viaEvent);
            foreach (var rule in selectedRule == null ? Array.Empty<ParsedPolicyRule>() : new[] { selectedRule }) {
                load.Ct.ThrowIfCancellationRequested();

                if (!IsStateMatch(rule.State, toState)) continue;
                if (rule.Via.HasValue) {
                    if (viaEvent == null || rule.Via.Value != viaEvent.Code) continue;
                }

                var ruleType = rule.Type ?? HookType.Gate;  // default: gate
                var ruleParamCodes = rule.ParamCodes;

                foreach (var emit in rule.Emits) {
                    load.Ct.ThrowIfCancellationRequested();

                    var emitType = emit.Type ?? ruleType;   // emit wins; else inherit from rule
                    // Emit.ParamCodes wins if non-empty; else rule.ParamCodes.
                    var effectiveCodes = emit.ParamCodes.Count > 0 ? emit.ParamCodes : ruleParamCodes;
                    var resolvedParams = ResolveParams(parsed.ParamCatalog, effectiveCodes);

                    specs.Add((emit.Route, emitType, emit.Group, emit.OrderSeq, emit.AckMode, emit.SendAlways,
                               emit.OnSuccess, emit.OnFailure,
                               emit.NotBefore, emit.Deadline,
                               resolvedParams));
                }
            }

            if (specs.Count == 0) return Array.Empty<ILifeCycleHookEmission>();

            var minOrder = specs.Min(s => s.orderSeq);
            var dispatchType = specs.Any(s => s.orderSeq == minOrder && s.hookType == HookType.Gate)
                ? HookType.Gate
                : HookType.Effect;

            // applied.LifeCycleId is always set when EmitHooksAsync is called after a successful transition.
            var lcId = applied.LifeCycleId ?? 0;

            var emissions = new List<ILifeCycleHookEmission>(specs.Count);
            foreach (var s in specs) {
                load.Ct.ThrowIfCancellationRequested();

                var hookId = await _dal.Hook.UpsertByKeyReturnIdAsync(
                    instanceId, applied.ToStateId, applied.EventId, true,
                    s.hookCode, s.hookType, s.emitGroup,
                    s.orderSeq, s.ackMode, s.sendAlways, load);

                // Create a hook_lc row for this hook + lifecycle entry (dispatched=0 initially).
                // All hooks (min-order and higher-order) get a hook_lc row so the order-advance
                // queries can find undispatched hooks for this lifecycle entry.
                var hookLcId = await _dal.HookLc.InsertReturnIdAsync(hookId, lcId, load);

                // Gate phase comes first for the first reached order. Same-order effects stay queued
                // until that order's gates are resolved; later orders stay queued until reached.
                var dispatchNow = s.orderSeq == minOrder && s.hookType == dispatchType;
                if (!dispatchNow) continue;

                emissions.Add(new LifeCycleHookEmission {
                    HookId = hookId,
                    HookLcId = hookLcId,
                    StateId = applied.ToStateId,
                    OnEntry = true,
                    Route = s.hookCode,
                    OnSuccessEvent = s.onSuccess,
                    OnFailureEvent = s.onFailure,
                    NotBefore = s.notBefore,
                    Deadline = s.deadline,
                    Params = s.resolvedParams,
                    HookType = s.hookType,
                    GroupName = s.emitGroup,
                    OrderSeq = s.orderSeq,
                    AckMode = s.ackMode
                });
            }

            return emissions;
        }

        private static IReadOnlyList<LifeCycleParamItem>? ResolveParams(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>?> catalog, IReadOnlyList<string> codes) {

            if (codes == null || codes.Count == 0) return null;

            var list = new List<LifeCycleParamItem>(codes.Count);
            foreach (var c in codes) {
                catalog.TryGetValue(c, out var data);
                list.Add(new LifeCycleParamItem { Code = c, Data = data ?? new Dictionary<string, object?>() });
            }
            return list;
        }

        private static bool IsStateMatch(string routeState, StateDef toState) {
            if (string.Equals(routeState, toState.Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(toState.DisplayName) && string.Equals(routeState, toState.DisplayName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Read hook type: "type" field first ("gate"/"effect"); fall back to boolean "blocking" for backward compat.
        private static HookType? ReadHookType(JsonElement el) {
            var typeStr = el.GetString(KEY_HOOK_TYPE);
            if (!string.IsNullOrWhiteSpace(typeStr))
                return string.Equals(typeStr, "effect", StringComparison.OrdinalIgnoreCase) ? HookType.Effect : HookType.Gate;
            var blocking = el.ReadOptionalBool(KEY_BLOCKING);
            if (blocking.HasValue) return blocking.Value ? HookType.Gate : HookType.Effect;
            return null;  // no value present — caller uses its own default
        }

        private static (string? onSuccess, string? onFailure) ReadCompletionEvents(JsonElement el) {
            if (!el.TryGetProperty(KEY_COMPLETE, out var compEl) || compEl.ValueKind != JsonValueKind.Object) return (null, null);
            string? onSuccess = null;
            string? onFailure = null;
            if (compEl.TryGetProperty(KEY_SUCCESS, out var sEl)) onSuccess = sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : sEl.ToString();
            if (compEl.TryGetProperty(KEY_FAILURE, out var fEl)) onFailure = fEl.ValueKind == JsonValueKind.String ? fEl.GetString() : fEl.ToString();
            return (onSuccess, onFailure);
        }

        // Selects the best matching rule for a given (toState, viaEvent) pair.
        // "Best" = prefers rules with a specific "via" clause over generic rules (no "via").
        // First rule wins among equally-specific candidates.
        private static ParsedPolicyRule? SelectBestRule(ParsedPolicy parsed, StateDef toState, EventDef? viaEvent) {
            ParsedPolicyRule? best = null;
            var bestHasVia = false;

            foreach (var rule in parsed.Rules) {
                if (!IsStateMatch(rule.State, toState)) continue;

                var hasVia = rule.Via.HasValue;
                if (hasVia) {
                    if (viaEvent == null || rule.Via!.Value != viaEvent.Code) continue;
                }

                if (best == null || hasVia || !bestHasVia) {
                    best = rule;
                    bestHasVia = hasVia;
                }
            }

            return best;
        }

        // Returns the rule-level context (params + completion events) for the given transition.
        // Pass policyId when available (e.g. from PolicyResolution) to get a cache hit.
        // AckManager calls this without a policyId (passes 0 = no cache); WorkFlowEngine passes the ID.
        public RuleContext ResolveRuleContextFromJson(string policyJson, StateDef toState, EventDef? viaEvent, CancellationToken ct = default, long policyId = 0) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(policyJson)) return new RuleContext();

            var parsed = GetOrAddCached(policyId, policyJson);
            var rule = SelectBestRule(parsed, toState, viaEvent);
            if (rule == null) return new RuleContext();

            return new RuleContext {
                OnSuccessEvent = rule.OnSuccess,
                OnFailureEvent = rule.OnFailure,
                Params = ResolveParams(parsed.ParamCatalog, rule.ParamCodes)
            };
        }

        // Returns the hook-level context (params + completion events + timing) for a specific hook route.
        // Emit-level settings override rule-level settings where specified (same inheritance as EmitHooksAsync).
        // Pass policyId when available to get a cache hit; 0 = parse without caching.
        public HookContext ResolveHookContextFromJson(string policyJson, StateDef toState, EventDef? viaEvent, string hookCode, CancellationToken ct = default, long policyId = 0) {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(policyJson) || string.IsNullOrWhiteSpace(hookCode)) return new HookContext();

            var parsed = GetOrAddCached(policyId, policyJson);
            var rule = SelectBestRule(parsed, toState, viaEvent);
            if (rule == null) return new HookContext();

            // Start from rule-level context.
            var hc = new HookContext {
                Params = ResolveParams(parsed.ParamCatalog, rule.ParamCodes)
            };

            // If a matching emit exists, override with emit-level settings.
            // ParamCodes: emit wins (no merge); else falls back to rule.
            var emit = rule.Emits.FirstOrDefault(e => string.Equals(e.Route, hookCode, StringComparison.OrdinalIgnoreCase));
            if (emit != null) {
                hc.OnSuccessEvent = emit.OnSuccess;     // already collapsed at parse time
                hc.OnFailureEvent = emit.OnFailure;
                var effectiveCodes = emit.ParamCodes.Count > 0 ? emit.ParamCodes : rule.ParamCodes;
                hc.Params = ResolveParams(parsed.ParamCatalog, effectiveCodes);
                hc.NotBefore = emit.NotBefore;
                hc.Deadline = emit.Deadline;
            }

            return hc;
        }
    }
}

