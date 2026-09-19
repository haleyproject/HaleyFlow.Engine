using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Haley.Enums;
using Haley.Models;

namespace Haley.Abstractions {
    internal interface IWorkFlowDAL : IDALUtilBase {
        IBlueprintReadDAL Blueprint { get; }
        IBlueprintWriteDAL BlueprintWrite { get; }
        IInstanceDAL Instance { get; }
        ILifeCycleDAL LifeCycle { get; }
        ILifeCycleDataDAL LifeCycleData { get; }
        IHookRouteDAL HookRoute { get; }
        IHookGroupDAL HookGroup { get; }
        IHookDAL Hook { get; }
        IHookLcDAL HookLc { get; }
        IConsumerDAL Consumer { get; }
        IAckDAL Ack { get; }
        IAckConsumerDAL AckConsumer { get; }
        ILcAckDAL LcAck { get; }
        IHookAckDAL HookAck { get; }
        IAckDispatchDAL AckDispatch { get; }
        IActivityDAL Activity { get; }
        IActivityStatusDAL ActivityStatus { get; }
        IRuntimeDAL Runtime { get; }
        IRuntimeDataDAL RuntimeData { get; }
        IEngineCareDAL EngineCare { get; }
        ILifeCycleTimeoutDAL LcTimeout { get; }
        ILcNextDAL LcNext { get; }
        IExecutionDAL Execution { get; }
    }
}
