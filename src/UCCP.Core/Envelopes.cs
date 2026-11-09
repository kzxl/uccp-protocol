using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UCCP.Core.Contracts.Envelopes
{
    public enum ExecutionStatus
    {
        Success,
        PendingApproval,
        Running,
        Failed,
        Cancelled
    }

    public class ExecutionOptions
    {
        public int TimeoutMs { get; set; } = 10000;
        public string ExecutionMode { get; set; } = "SYNC";
        public bool CompactResponse { get; set; } = true;
    }

    public class UccpRequestEnvelope
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion { get; set; } = "1.0";

        [JsonProperty("requestId")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString("D");

        [JsonProperty("correlationId")]
        public string CorrelationId { get; set; }

        [JsonProperty("capabilityId")]
        public string CapabilityId { get; set; }

        [JsonProperty("contextHandle")]
        public string ContextHandle { get; set; }

        [JsonProperty("idempotencyKey")]
        public string IdempotencyKey { get; set; }

        [JsonProperty("options")]
        public ExecutionOptions Options { get; set; } = new ExecutionOptions();

        [JsonProperty("payload")]
        public JObject Payload { get; set; } = new JObject();
    }

    public class UccpResponseEnvelope
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion { get; set; } = "1.0";

        [JsonProperty("requestId")]
        public string RequestId { get; set; }

        [JsonProperty("correlationId")]
        public string CorrelationId { get; set; }

        [JsonProperty("status")]
        public ExecutionStatus Status { get; set; }

        [JsonProperty("executionDurationMs")]
        public double ExecutionDurationMs { get; set; }

        [JsonProperty("contextHandle")]
        public string ContextHandle { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }

        [JsonProperty("error")]
        public UccpError Error { get; set; }
    }

    public class UccpError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("retryable")]
        public bool Retryable { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("remediation")]
        public string Remediation { get; set; }

        [JsonProperty("details")]
        public JObject Details { get; set; }
    }
}
