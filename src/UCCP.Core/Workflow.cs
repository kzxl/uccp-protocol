using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Execution;

namespace UCCP.Core.Contracts.Workflow
{
    public class WorkflowStep
    {
        public string StepId { get; set; }
        public string CapabilityId { get; set; }
        public List<string> DependsOn { get; set; } = new List<string>();
        public Func<Dictionary<string, ExecutionResult>, JObject, JObject> InputMapper { get; set; }
        public Func<Dictionary<string, ExecutionResult>, bool> Condition { get; set; }
        public string CompensatingCapabilityId { get; set; }
        public Func<ExecutionResult, JObject> CompensationInputMapper { get; set; }
    }

    public class WorkflowDefinition
    {
        public string WorkflowId { get; set; }
        public string Name { get; set; }
        public List<WorkflowStep> Steps { get; set; } = new List<WorkflowStep>();
    }

    public interface IWorkflowEngine
    {
        Task<ExecutionResult> ExecuteWorkflowAsync(
            WorkflowDefinition workflow,
            JObject initialInput,
            ExecutionContextData context,
            CancellationToken cancellationToken);
    }
}
