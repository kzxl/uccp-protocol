using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UCCP.Core.Contracts.Envelopes;
using UCCP.Core.Contracts.Execution;
using UCCP.Runtime.Registry;

namespace UCCP.Adapters.Mcp
{
    public class McpServerDispatcher
    {
        private readonly IUccpRuntime _runtime;
        private readonly InMemoryCapabilityRegistry _registry;

        public McpServerDispatcher(IUccpRuntime runtime, InMemoryCapabilityRegistry registry)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public async Task<McpResponse> DispatchAsync(
            McpRequest request,
            ClaimsPrincipal principal = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null)
            {
                return new McpResponse
                {
                    Error = new McpError { Code = -32600, Message = "Invalid Request: request object is null" }
                };
            }

            try
            {
                switch (request.Method)
                {
                    case "tools/list":
                        var tools = await HandleToolsListAsync(principal).ConfigureAwait(false);
                        return new McpResponse
                        {
                            Id = request.Id,
                            Result = new { tools = tools }
                        };

                    case "tools/call":
                        var callResult = await HandleToolsCallAsync(request.Params, principal, cancellationToken).ConfigureAwait(false);
                        return new McpResponse
                        {
                            Id = request.Id,
                            Result = callResult
                        };

                    default:
                        return new McpResponse
                        {
                            Id = request.Id,
                            Error = new McpError { Code = -32601, Message = $"Method not found: {request.Method}" }
                        };
                }
            }
            catch (Exception ex)
            {
                return new McpResponse
                {
                    Id = request.Id,
                    Error = new McpError { Code = -32603, Message = $"Internal error: {ex.Message}" }
                };
            }
        }

        private async Task<List<McpTool>> HandleToolsListAsync(ClaimsPrincipal principal)
        {
            var tools = new List<McpTool>
            {
                // Progressive Discovery Stage 1: List domains
                new McpTool
                {
                    Name = "uccp_list_domains",
                    Description = "Progressive Discovery Stage 1: Returns all available enterprise business domains and their capability counts.",
                    InputSchema = JObject.Parse(@"{
                        ""type"": ""object"",
                        ""properties"": {}
                    }")
                },
                // Progressive Discovery Stage 2: Search capabilities
                new McpTool
                {
                    Name = "uccp_search_capabilities",
                    Description = "Progressive Discovery Stage 2: Searches for relevant enterprise capabilities by business domain or keywords without bloating prompts.",
                    InputSchema = JObject.Parse(@"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""domain"": { ""type"": ""string"", ""description"": ""Target business domain (e.g. 'inventory', 'purchasing') or '*' for all."" },
                            ""query"": { ""type"": ""string"", ""description"": ""Keywords describing user intent (e.g. 'check stock', 'create po')."" }
                        },
                        ""required"": [""domain""]
                    }")
                },
                // Progressive Discovery Stage 3: Lazy Schema Hydration
                new McpTool
                {
                    Name = "uccp_get_capability_schema",
                    Description = "Progressive Discovery Stage 3: Retrieves the detailed input and output JSON schema for a target capability before calling it.",
                    InputSchema = JObject.Parse(@"{
                        ""type"": ""object"",
                        ""properties"": {
                            ""capabilityId"": { ""type"": ""string"", ""description"": ""Target capability identifier (e.g. 'inventory.material.stock.query@v1.0')."" }
                        },
                        ""required"": [""capabilityId""]
                    }")
                }
            };

            return tools;
        }

        private async Task<CallToolResult> HandleToolsCallAsync(
            JObject parameters,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
        {
            if (parameters == null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new ContentBlock { Text = "Parameters object is missing." } }
                };
            }

            var toolName = parameters["name"]?.ToString();
            var arguments = parameters["arguments"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(toolName))
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new ContentBlock { Text = "Tool name is required." } }
                };
            }

            // Handle Meta-Tool 1: List Domains
            if (toolName.Equals("uccp_list_domains", StringComparison.OrdinalIgnoreCase))
            {
                var domains = await _registry.GetDomainsAsync(principal).ConfigureAwait(false);
                var text = Newtonsoft.Json.JsonConvert.SerializeObject(domains, Newtonsoft.Json.Formatting.Indented);
                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new ContentBlock { Text = text } }
                };
            }

            // Handle Meta-Tool 2: Search Capabilities
            if (toolName.Equals("uccp_search_capabilities", StringComparison.OrdinalIgnoreCase))
            {
                var domain = arguments["domain"]?.ToString();
                var query = arguments["query"]?.ToString();

                var capabilities = await _registry.SearchCapabilitiesAsync(domain, query, principal).ConfigureAwait(false);
                var text = Newtonsoft.Json.JsonConvert.SerializeObject(capabilities, Newtonsoft.Json.Formatting.Indented);
                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new ContentBlock { Text = text } }
                };
            }

            // Handle Meta-Tool 3: Get Schema on-demand
            if (toolName.Equals("uccp_get_capability_schema", StringComparison.OrdinalIgnoreCase))
            {
                var capId = arguments["capabilityId"]?.ToString();
                if (string.IsNullOrEmpty(capId))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock> { new ContentBlock { Text = "capabilityId parameter is required." } }
                    };
                }

                var descriptor = await _registry.GetDescriptorAsync(capId).ConfigureAwait(false);
                if (descriptor == null)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock> { new ContentBlock { Text = $"Capability '{capId}' not found." } }
                    };
                }

                var schemaDoc = new JObject
                {
                    ["id"] = descriptor.Id,
                    ["name"] = descriptor.Name,
                    ["domain"] = descriptor.Domain,
                    ["description"] = descriptor.Description,
                    ["action"] = descriptor.Action.ToString(),
                    ["inputSchema"] = descriptor.InputSchema ?? new JObject(),
                    ["outputSchema"] = descriptor.OutputSchema ?? new JObject()
                };

                return new CallToolResult
                {
                    Content = new List<ContentBlock> { new ContentBlock { Text = Newtonsoft.Json.JsonConvert.SerializeObject(schemaDoc, Newtonsoft.Json.Formatting.Indented) } }
                };
            }

            // Handle Concrete UCCP Capability Invocation
            var uccpRequest = new UccpRequestEnvelope
            {
                CapabilityId = toolName,
                Payload = arguments,
                ContextHandle = arguments["contextHandle"]?.ToString(),
                IdempotencyKey = arguments["idempotencyKey"]?.ToString()
            };

            var response = await _runtime.ExecuteAsync(uccpRequest, principal, cancellationToken).ConfigureAwait(false);

            if (response.Status == ExecutionStatus.Success)
            {
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(response.Summary))
                {
                    sb.AppendLine(response.Summary);
                }

                if (!string.IsNullOrEmpty(response.ContextHandle))
                {
                    sb.AppendLine($"[ContextHandle: {response.ContextHandle}]");
                }

                if (response.Data != null && response.Data.Count > 0)
                {
                    sb.AppendLine($"[Data: {Newtonsoft.Json.JsonConvert.SerializeObject(response.Data)}]");
                }

                var outputText = sb.Length > 0 ? sb.ToString().TrimEnd() : "Execution completed successfully.";

                return new CallToolResult
                {
                    Content = new List<ContentBlock>
                    {
                        new ContentBlock { Text = outputText }
                    }
                };
            }

            if (response.Status == ExecutionStatus.PendingApproval)
            {
                return new CallToolResult
                {
                    Content = new List<ContentBlock>
                    {
                        new ContentBlock { Text = response.Summary }
                    }
                };
            }

            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new ContentBlock { Text = $"[Execution Failed: {response.Error?.Code}] {response.Error?.Message}" }
                }
            };
        }
    }
}
