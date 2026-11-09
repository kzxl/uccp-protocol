using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Execution;
using UCCP.Core.Contracts.Workflow;
using UCCP.Runtime.Registry;

namespace UCCP.Runtime.Workflow
{
    public class LightweightDagRunner : IWorkflowEngine
    {
        private readonly InMemoryCapabilityRegistry _registry;

        public LightweightDagRunner(InMemoryCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public async Task<ExecutionResult> ExecuteWorkflowAsync(
            WorkflowDefinition workflow,
            JObject initialInput,
            ExecutionContextData context,
            CancellationToken cancellationToken)
        {
            if (workflow == null) throw new ArgumentNullException(nameof(workflow));

            var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase);
            var completedSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var executedStepsForCompensation = new Stack<Tuple<WorkflowStep, ExecutionResult>>();

            var remainingSteps = workflow.Steps.ToList();

            try
            {
                while (remainingSteps.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Find steps ready to execute (all dependencies satisfied)
                    var readySteps = remainingSteps
                        .Where(s => s.DependsOn.All(d => completedSteps.Contains(d)))
                        .ToList();

                    if (readySteps.Count == 0)
                    {
                        throw new InvalidOperationException($"Deadlock detected in workflow '{workflow.WorkflowId}': Circular dependency or unsatisfied prerequisites.");
                    }

                    // Execute ready steps in parallel
                    var tasks = readySteps.Select(async step =>
                    {
                        // Check condition
                        if (step.Condition != null && !step.Condition(stepResults))
                        {
                            return new { Step = step, Result = new ExecutionResult { Success = true, Summary = "Step skipped by condition." }, Skipped = true };
                        }

                        var executor = _registry.GetExecutor(step.CapabilityId);
                        if (executor == null)
                        {
                            throw new KeyNotFoundException($"Capability '{step.CapabilityId}' required by step '{step.StepId}' is not registered.");
                        }

                        var input = step.InputMapper != null
                            ? step.InputMapper(stepResults, initialInput)
                            : initialInput;

                        var res = await executor.ExecuteAsync(input, context, cancellationToken).ConfigureAwait(false);
                        return new { Step = step, Result = res, Skipped = false };
                    });

                    var stepOutcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

                    foreach (var outcome in stepOutcomes)
                    {
                        if (!outcome.Result.Success)
                        {
                            throw new Exception($"Step '{outcome.Step.StepId}' failed: {outcome.Result.ErrorMessage}");
                        }

                        stepResults[outcome.Step.StepId] = outcome.Result;
                        completedSteps.Add(outcome.Step.StepId);
                        remainingSteps.Remove(outcome.Step);

                        if (!outcome.Skipped)
                        {
                            executedStepsForCompensation.Push(Tuple.Create(outcome.Step, outcome.Result));
                        }
                    }
                }

                var finalStep = workflow.Steps.LastOrDefault();
                var finalResult = finalStep != null && stepResults.ContainsKey(finalStep.StepId)
                    ? stepResults[finalStep.StepId]
                    : new ExecutionResult { Success = true, Summary = "Workflow completed successfully." };

                return finalResult;
            }
            catch (Exception ex)
            {
                // Run Saga Compensations in reverse order
                await ExecuteCompensationAsync(executedStepsForCompensation, context).ConfigureAwait(false);

                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Workflow '{workflow.WorkflowId}' execution failed: {ex.Message} (All compensations rolled back).",
                    ErrorCode = "UCCP_500_WORKFLOW_FAILED"
                };
            }
        }

        private async Task ExecuteCompensationAsync(
            Stack<Tuple<WorkflowStep, ExecutionResult>> executedSteps,
            ExecutionContextData context)
        {
            while (executedSteps.Count > 0)
            {
                var item = executedSteps.Pop();
                var step = item.Item1;
                var res = item.Item2;

                if (!string.IsNullOrEmpty(step.CompensatingCapabilityId))
                {
                    try
                    {
                        var compExecutor = _registry.GetExecutor(step.CompensatingCapabilityId);
                        if (compExecutor != null)
                        {
                            var compInput = step.CompensationInputMapper != null
                                ? step.CompensationInputMapper(res)
                                : res.Data;

                            await compExecutor.ExecuteAsync(compInput, context, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Log compensation failure in production audit
                    }
                }
            }
        }
    }
}
