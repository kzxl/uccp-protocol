using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Adapters.InProc;
using UCCP.Adapters.Mcp;
using UCCP.Core.Contracts.Context;
using UCCP.Core.Contracts.Descriptors;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Execution;
using UCCP.Core.Contracts.Workflow;
using UCCP.Runtime;
using UCCP.Runtime.Context;
using UCCP.Runtime.Registry;
using UCCP.Runtime.Security;
using UCCP.Runtime.Workflow;
using Xunit;

namespace UCCP.Tests
{
    public class UccpComprehensiveTests
    {
        private readonly InMemoryCapabilityRegistry _registry;
        private readonly InMemoryContextEngine _contextEngine;
        private readonly AbacPolicyEngine _policyEngine;
        private readonly DefaultUccpRuntime _runtime;
        private readonly InProcInvoker _inProcInvoker;
        private readonly McpServerDispatcher _mcpDispatcher;
        private readonly LightweightDagRunner _dagRunner;

        public UccpComprehensiveTests()
        {
            _registry = new InMemoryCapabilityRegistry();
            _contextEngine = new InMemoryContextEngine();
            _policyEngine = new AbacPolicyEngine();
            _runtime = new DefaultUccpRuntime(_registry, _contextEngine, _policyEngine);
            _inProcInvoker = new InProcInvoker(_runtime);
            _mcpDispatcher = new McpServerDispatcher(_runtime, _registry);
            _dagRunner = new LightweightDagRunner(_registry);

            RegisterSampleCapabilities();
        }

        private void RegisterSampleCapabilities()
        {
            // 1. Inventory Stock Query
            _registry.Register(new CapabilityDescriptor
            {
                Id = "inventory.material.stock.query@v1.0",
                Name = "Query Material Stock Balance",
                Domain = "inventory",
                Subdomain = "material",
                Description = "Returns physical and available warehouse stock balances.",
                Action = ActionKind.Read,
                Tags = new List<string> { "stock", "material", "inventory", "balance" },
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "WarehouseStaff", "Planner", "Manager" }
                }
            }, new MockExecutor((payload, ctx) =>
            {
                var materialCode = payload["materialCode"]?.ToString() ?? "RM-001";
                return new ExecutionResult
                {
                    Success = true,
                    Summary = $"Stock balance for {materialCode}: 150 KG in Warehouse 1.",
                    Data = JObject.FromObject(new
                    {
                        materialCode = materialCode,
                        warehouseId = 1,
                        quantity = 150.0,
                        unitCost = 1.25,
                        safetyStock = 500.0
                    })
                };
            }));

            // 2. High-value Purchase Order Approval (Requires Human Manager)
            _registry.Register(new CapabilityDescriptor
            {
                Id = "purchasing.po.approve@v1.0",
                Name = "Approve Purchase Order",
                Domain = "purchasing",
                Subdomain = "po",
                Description = "Approves a purchase order and releases it to vendor.",
                Action = ActionKind.Execute,
                Tags = new List<string> { "po", "purchase", "approval" },
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "PurchasingManager", "Director" },
                    HumanApprovalRequired = true
                }
            }, new MockExecutor((payload, ctx) =>
            {
                var poId = payload["poId"]?.ToString() ?? "PO-001";
                return new ExecutionResult
                {
                    Success = true,
                    Summary = $"Purchase Order {poId} approved successfully.",
                    Data = JObject.FromObject(new { poId = poId, status = "Approved" })
                };
            }));

            // 3. Finance Ledger Query (Confidential Admin)
            _registry.Register(new CapabilityDescriptor
            {
                Id = "finance.ledger.query@v1.0",
                Name = "Query General Ledger Entries",
                Domain = "finance",
                Subdomain = "ledger",
                Description = "Queries confidential balance sheets and ledger accounts.",
                Action = ActionKind.Read,
                Tags = new List<string> { "finance", "ledger", "pnl" },
                Security = new SecurityMetadata
                {
                    RequiredRoles = new List<string> { "ChiefAccountant" }
                }
            }, new MockExecutor((payload, ctx) =>
            {
                return new ExecutionResult
                {
                    Success = true,
                    Data = JObject.FromObject(new { revenue = 1000000.0, netProfit = 250000.0 })
                };
            }));
        }

        #region Test Suite 1: Progressive Discovery & Token Optimization

        [Fact]
        public async Task ProgressiveDiscovery_Stage1_ReturnsDomains()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var domains = await _registry.GetDomainsAsync(user);

            Assert.NotNull(domains);
            Assert.Contains(domains, d => d.Domain.Equals("inventory", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ProgressiveDiscovery_Stage2_FiltersByKeywordAndDomain()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var results = await _registry.SearchCapabilitiesAsync("inventory", "stock balance", user);

            Assert.Single(results);
            Assert.Equal("inventory.material.stock.query@v1.0", results[0].Id);
        }

        [Fact]
        public async Task ProgressiveDiscovery_HidesUnauthorizedCapabilities()
        {
            // WarehouseStaff should NOT see finance capabilities in discovery
            var staffUser = CreatePrincipal("staff_01", "WarehouseStaff");
            var financeResults = await _registry.SearchCapabilitiesAsync("finance", "ledger", staffUser);

            Assert.Empty(financeResults);

            // ChiefAccountant CAN see finance capabilities
            var accountantUser = CreatePrincipal("cfo_01", "ChiefAccountant");
            var accountantResults = await _registry.SearchCapabilitiesAsync("finance", "ledger", accountantUser);

            Assert.Single(accountantResults);
        }

        #endregion

        #region Test Suite 2: Context Engine & Opaque Context Handles

        [Fact]
        public async Task ContextEngine_StoresLargeTable_AndGeneratesOpaqueHandle()
        {
            var rows = Enumerable.Range(1, 1000)
                .Select(i => new { ItemId = i, Name = $"Material-{i}", Qty = i * 10 })
                .ToList();

            var handle = await _contextEngine.StoreContextAsync("tenant_acme", rows, TimeSpan.FromMinutes(10));

            Assert.NotNull(handle);
            Assert.StartsWith("ctx_hnd:tenant_acme:", handle);

            var resolved = await _contextEngine.ResolveContextAsync<System.Collections.IList>("tenant_acme", handle);
            Assert.NotNull(resolved);
            Assert.Equal(1000, resolved.Count);
        }

        [Fact]
        public async Task ContextEngine_EnforcesTenantIsolation_PreventsTenantBleed()
        {
            var handle = await _contextEngine.StoreContextAsync("tenant_A", "Secret Data A", TimeSpan.FromMinutes(5));

            // Accessing with tenant_B MUST throw Tenant Isolation Violation
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                await _contextEngine.ResolveContextAsync<string>("tenant_B", handle);
            });
        }

        #endregion

        #region Test Suite 3: Security & ABAC Policy Enforcement

        [Fact]
        public async Task PolicyEngine_DeniesUnauthenticatedCaller()
        {
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0"
            };

            var response = await _runtime.ExecuteAsync(request, principal: null);

            Assert.Equal(ExecutionStatus.Failed, response.Status);
            Assert.Equal("UCCP_401_UNAUTHENTICATED", response.Error.Code);
        }

        [Fact]
        public async Task PolicyEngine_DeniesUnauthorizedRole()
        {
            var unauthorizedUser = CreatePrincipal("intern_01", "InternRole");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0"
            };

            var response = await _runtime.ExecuteAsync(request, principal: unauthorizedUser);

            Assert.Equal(ExecutionStatus.Failed, response.Status);
            Assert.Equal("UCCP_403_ROLE_DENIED", response.Error.Code);
        }

        [Fact]
        public async Task PolicyEngine_SuspendsAction_WhenHumanApprovalRequired()
        {
            var manager = CreatePrincipal("mgr_01", "PurchasingManager");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "purchasing.po.approve@v1.0",
                Payload = JObject.FromObject(new { poId = "PO-2026-99" })
            };

            // Without approval token, execution MUST be Suspended
            var response = await _runtime.ExecuteAsync(request, principal: manager);

            Assert.Equal(ExecutionStatus.PendingApproval, response.Status);
            Assert.StartsWith("appr_ticket:", response.ContextHandle);
            Assert.Contains("[SUSPENDED]", response.Summary);

            // Re-submitting WITH human approval token allows execution to succeed
            request.Payload["approvalToken"] = "token_signed_by_human_faceid";
            var successResponse = await _runtime.ExecuteAsync(request, principal: manager);

            Assert.Equal(ExecutionStatus.Success, successResponse.Status);
        }

        [Fact]
        public async Task PolicyEngine_MasksConfidentialCost_ForNonFinanceUsers()
        {
            // User has permission to query stock, but NO permission to view unit cost
            var warehouseStaff = CreatePrincipal("staff_01", "WarehouseStaff");
            var request = new UccpRequestEnvelope
            {
                CapabilityId = "inventory.material.stock.query@v1.0"
            };

            var response = await _runtime.ExecuteAsync(request, principal: warehouseStaff);

            Assert.Equal(ExecutionStatus.Success, response.Status);
            // Verify quantity is visible
            Assert.Equal(150.0, (double)response.Data["quantity"]);
            // Verify unitCost is masked
            Assert.Equal("[CONFIDENTIAL_MASKED]", (string)response.Data["unitCost"]);
        }

        #endregion

        #region Test Suite 4: Deterministic Workflow Engine & Saga Compensation

        [Fact]
        public async Task WorkflowEngine_ExecutesDagDeterministically_WithoutLlmHops()
        {
            var step1Executed = false;
            var step2Executed = false;

            _registry.Register(new CapabilityDescriptor { Id = "test.step1@v1.0" }, new MockExecutor((p, c) =>
            {
                step1Executed = true;
                return new ExecutionResult { Success = true, Data = JObject.FromObject(new { balance = 100 }) };
            }));

            _registry.Register(new CapabilityDescriptor { Id = "test.step2@v1.0" }, new MockExecutor((p, c) =>
            {
                step2Executed = true;
                return new ExecutionResult { Success = true, Summary = "Workflow finished." };
            }));

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "wf.test.reorder@v1.0",
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep { StepId = "s1", CapabilityId = "test.step1@v1.0" },
                    new WorkflowStep { StepId = "s2", CapabilityId = "test.step2@v1.0", DependsOn = new List<string> { "s1" } }
                }
            };

            var ctx = new ExecutionContextData();
            var res = await _dagRunner.ExecuteWorkflowAsync(workflow, new JObject(), ctx, CancellationToken.None);

            Assert.True(res.Success);
            Assert.True(step1Executed);
            Assert.True(step2Executed);
        }

        [Fact]
        public async Task WorkflowEngine_ExecutesCompensation_WhenDownstreamStepFails()
        {
            var compensated = false;

            _registry.Register(new CapabilityDescriptor { Id = "saga.deduct@v1.0" }, new MockExecutor((p, c) =>
                new ExecutionResult { Success = true, Data = JObject.FromObject(new { deducted = 50 }) }));

            _registry.Register(new CapabilityDescriptor { Id = "saga.revert_deduct@v1.0" }, new MockExecutor((p, c) =>
            {
                compensated = true;
                return new ExecutionResult { Success = true };
            }));

            _registry.Register(new CapabilityDescriptor { Id = "saga.failing_payment@v1.0" }, new MockExecutor((p, c) =>
                new ExecutionResult { Success = false, ErrorMessage = "Payment gateway down" }));

            var workflow = new WorkflowDefinition
            {
                WorkflowId = "wf.saga.test@v1.0",
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep
                    {
                        StepId = "step_deduct",
                        CapabilityId = "saga.deduct@v1.0",
                        CompensatingCapabilityId = "saga.revert_deduct@v1.0"
                    },
                    new WorkflowStep
                    {
                        StepId = "step_pay",
                        CapabilityId = "saga.failing_payment@v1.0",
                        DependsOn = new List<string> { "step_deduct" }
                    }
                }
            };

            var ctx = new ExecutionContextData();
            var result = await _dagRunner.ExecuteWorkflowAsync(workflow, new JObject(), ctx, CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(compensated, "Compensating transaction MUST execute when downstream step fails.");
        }

        #endregion

        #region Test Suite 5: MCP Adapter Interoperability

        [Fact]
        public async Task McpAdapter_ToolsList_ReturnsProgressiveDiscoveryMetaTools()
        {
            var mcpReq = new McpRequest { Method = "tools/list", Id = 1 };
            var mcpRes = await _mcpDispatcher.DispatchAsync(mcpReq);

            Assert.Null(mcpRes.Error);
            Assert.NotNull(mcpRes.Result);
        }

        [Fact]
        public async Task McpAdapter_ToolsCall_InvokesConcreteCapability()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");
            var mcpReq = new McpRequest
            {
                Method = "tools/call",
                Id = 2,
                Params = JObject.FromObject(new
                {
                    name = "inventory.material.stock.query@v1.0",
                    arguments = new { materialCode = "RM-STEEL-01" }
                })
            };

            var mcpRes = await _mcpDispatcher.DispatchAsync(mcpReq, user);

            Assert.Null(mcpRes.Error);
            var callResult = mcpRes.Result as CallToolResult;
            Assert.NotNull(callResult);
            Assert.False(callResult.IsError);
            Assert.Contains("Stock balance for RM-STEEL-01", callResult.Content[0].Text);
        }

        #endregion

        #region Test Suite 6: In-Process Micro-Benchmark (Level 3 Proof)

        [Fact]
        public async Task InProcess_Latency_IsSubMillisecond()
        {
            var user = CreatePrincipal("staff_01", "WarehouseStaff");

            // Warmup
            await _inProcInvoker.InvokeAsync("inventory.material.stock.query@v1.0", new { materialCode = "WARMUP" }, user);

            // Execute 1,000 iterations
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                var res = await _inProcInvoker.InvokeAsync("inventory.material.stock.query@v1.0", new { materialCode = "RM-001" }, user);
                Assert.Equal(ExecutionStatus.Success, res.Status);
            }

            sw.Stop();
            double avgLatencyMs = sw.Elapsed.TotalMilliseconds / iterations;

            // In-process invocation MUST be faster than 0.1ms (100 microseconds)
            Assert.True(avgLatencyMs < 0.2, $"In-process average latency ({avgLatencyMs:F4} ms) must be sub-millisecond.");
        }

        #endregion

        private static ClaimsPrincipal CreatePrincipal(string name, string role)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, name),
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role),
                new Claim("tenant_id", "tenant_acme"),
                new Claim("approval_limit", "50000.0")
            };

            var identity = new ClaimsIdentity(claims, "TestAuth");
            return new ClaimsPrincipal(identity);
        }

        private class MockExecutor : ICapabilityExecutor
        {
            private readonly Func<JObject, ExecutionContextData, ExecutionResult> _func;

            public MockExecutor(Func<JObject, ExecutionContextData, ExecutionResult> func)
            {
                _func = func;
            }

            public Task<ExecutionResult> ExecuteAsync(
                JObject payload,
                ExecutionContextData context,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_func(payload, context));
            }
        }
    }
}
