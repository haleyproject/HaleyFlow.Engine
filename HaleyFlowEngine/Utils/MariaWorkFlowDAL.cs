using Haley.Abstractions;
using Haley.Models;
using static Haley.Internal.QueryFields;
using Haley.Internal;

namespace Haley.Utils {
    internal class MariaWorkFlowDAL : DALUtilBase, IWorkFlowDAL {
        public IBlueprintReadDAL Blueprint { get; }
        public IBlueprintWriteDAL BlueprintWrite { get; }
        public IInstanceDAL Instance { get; }
        public ILifeCycleDAL LifeCycle { get; }
        public ILifeCycleDataDAL LifeCycleData { get; }
        public IHookRouteDAL HookRoute { get; }
        public IHookGroupDAL HookGroup { get; }
        public IHookDAL Hook { get; }
        public IHookLcDAL HookLc { get; }
        public IAckDAL Ack { get; }
        public IAckConsumerDAL AckConsumer { get; }
        public ILcAckDAL LcAck { get; }
        public IHookAckDAL HookAck { get; }
        public IAckDispatchDAL AckDispatch { get; }
        public IActivityDAL Activity { get; }
        public IActivityStatusDAL ActivityStatus { get; }
        public IRuntimeDAL Runtime { get; }
        public IRuntimeDataDAL RuntimeData { get; }
        public IConsumerDAL Consumer { get; }
        public IEngineCareDAL EngineCare { get; }
        public ILifeCycleTimeoutDAL LcTimeout { get; }
        public ILcNextDAL LcNext { get; }
        public IExecutionDAL Execution { get; }
        public MariaWorkFlowDAL(IAdapterGateway agw, string key) : base(agw, key) {
            Blueprint = new MariaBlueprintReadDAL(this);
            BlueprintWrite = new MariaBlueprintWriteDAL(this);
            Instance = new MariaInstanceDAL(this);
            LifeCycle = new MariaLifeCycleDAL(this);
            LifeCycleData = new MariaLifeCycleDataDAL(this);
            HookRoute = new MariaHookRouteDAL(this);
            HookGroup = new MariaHookGroupDAL(this);
            Hook = new MariaHookDAL(this);
            HookLc = new MariaHookLcDAL(this);

            Ack = new MariaAckDAL(this);
            AckConsumer = new MariaAckConsumerDAL(this);
            LcAck = new MariaLcAckDAL(this);
            HookAck = new MariaHookAckDAL(this);
            AckDispatch = new MariaAckDispatchDAL(this);

            Activity = new MariaActivityDAL(this);
            ActivityStatus = new MariaActivityStatusDAL(this);

            Runtime = new MariaRuntimeDAL(this);
            RuntimeData = new MariaRuntimeDataDAL(this);
            Consumer = new MariaConsumerDAL(this);
            EngineCare = new MariaEngineCareDAL(this);
            LcTimeout = new MariaLifeCycleTimeoutDAL(this);
            LcNext = new MariaLcNextDAL(this);
            Execution = new MariaExecutionDAL(this);
        }
    }
}
