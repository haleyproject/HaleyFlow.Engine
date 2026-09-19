using Haley.Models;
using Haley.Abstractions;
using Haley.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Models {
    internal sealed class LifeCycleHookEvent : LifeCycleEvent, ILifeCycleHookEvent {
        public override LifeCycleEventKind Kind => LifeCycleEventKind.Hook;
        public bool OnEntry { get; set; }
        public long LifeCycleId { get; set; }
        public string Route { get; set; }
        public DateTimeOffset? NotBefore { get; set; }
        public DateTimeOffset? Deadline { get; set; }
        public HookType HookType { get; set; } = HookType.Gate;
        public string? GroupName { get; set; }
        public int OrderSeq { get; set; } = 1;
        public int AckMode  { get; set; } = 0;
        public int RunCount { get; set; } = 1;
        public LifeCycleHookEvent() { }
        public LifeCycleHookEvent(LifeCycleEvent evt) : base(evt) { }
    }
}
