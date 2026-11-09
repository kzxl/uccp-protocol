# UCCP Technical Specification (.NET Framework 4.6.2 & .NET 8)

---

## 1. Compliance & Language Constraints

This specification defines the formal programming contracts for the Universal Context & Capability Protocol (UCCP). 

To ensure seamless integration into legacy enterprise monoliths while preserving modern cloud portability, the core library complies with:
- **Language Level**: Strictly C# 6.0 compatible (no records, no pattern matching switch expressions, no default interface methods).
- **Target Frameworks**: Dual-targeted for `.NET Framework 4.6.2` (Windows desktop / IIS) and `.NET Standard 2.0` / `.NET 8.0` (Linux containers / cloud services).
- **Zero Third-Party Leaks**: UCCP Core depends strictly on `Newtonsoft.Json` (or `System.Text.Json`). It has **zero** dependencies on DevExpress, Entity Framework, or ASP.NET UI controls.

---

## 2. Core Interface Contracts (C# 6.0)

### 2.1. The Execution Runtime (`IUccpRuntime`)
```csharp
namespace UCCP.Core.Contracts.Runtime
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using UCCP.Core.Contracts.Envelopes;

    /// <summary>
    /// Core deterministic execution engine.
    /// </summary>
    public interface IUccpRuntime
    {
        /// <summary>
        /// Executes a validated and authorized capability envelope.
        /// </summary>
        Task<UccpResponseEnvelope> ExecuteAsync(
            UccpRequestEnvelope request, 
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Resolves an active execution state or suspended approval ticket.
        /// </summary>
        Task<ExecutionStatusInfo> GetStatusAsync(
            string executionId, 
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
```

### 2.2. The Capability Registry (`ICapabilityRegistry`)
```csharp
namespace UCCP.Core.Contracts.Registry
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using UCCP.Core.Contracts.Descriptors;

    public interface ICapabilityRegistry
    {
        void Register(CapabilityDescriptor descriptor, ICapabilityExecutor executor);

        Task<CapabilityDescriptor> GetDescriptorAsync(string capabilityId);

        Task<IReadOnlyList<DomainSummary>> GetDomainsAsync();

        Task<IReadOnlyList<CapabilitySummary>> SearchCapabilitiesAsync(
            string domain, 
            string query, 
            int maxResults = 10);
    }
}
```

### 2.3. The Policy Engine (`IPolicyEngine`)
```csharp
namespace UCCP.Core.Contracts.Security
{
    using System.Security.Claims;
    using System.Threading.Tasks;
    using UCCP.Core.Contracts.Descriptors;
    using UCCP.Core.Contracts.Envelopes;

    public interface IPolicyEngine
    {
        Task<PolicyDecision> EvaluateAsync(
            ClaimsPrincipal principal, 
            CapabilityDescriptor capability, 
            UccpRequestEnvelope request);
    }

    public class PolicyDecision
    {
        public bool IsAllowed { get; set; }
        public string DenialReason { get; set; }
        public string RemediationCode { get; set; }
        public bool RequiresHumanApproval { get; set; }

        public static PolicyDecision Allow()
        {
            return new PolicyDecision { IsAllowed = true };
        }

        public static PolicyDecision Deny(string reason, string remediationCode = null)
        {
            return new PolicyDecision 
            { 
                IsAllowed = false, 
                DenialReason = reason, 
                RemediationCode = remediationCode 
            };
        }
    }
}
```

### 2.4. The Context Engine (`IContextEngine`)
```csharp
namespace UCCP.Core.Contracts.Context
{
    using System;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public interface IContextEngine
    {
        Task<string> StoreContextAsync(
            string tenantId, 
            object data, 
            TimeSpan ttl);

        Task<T> ResolveContextAsync<T>(
            string tenantId, 
            string contextHandle);

        Task<ExecutionContextData> HydrateExecutionContextAsync(
            ClaimsPrincipal principal, 
            string contextHandle);
    }
}
```

### 2.5. The Capability Executor (`ICapabilityExecutor`)
```csharp
namespace UCCP.Core.Contracts.Execution
{
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using UCCP.Core.Contracts.Envelopes;

    public interface ICapabilityExecutor
    {
        Task<ExecutionResult> ExecuteAsync(
            JObject payload, 
            ExecutionContextData context, 
            CancellationToken cancellationToken);
    }
}
```

---

## 3. Extension Architecture

Developers can customize UCCP behavior by registering custom implementations via Autofac or Microsoft DI:

1. **`IUccpTransportAdapter`**: Pluggable transport listener (e.g., Kafka consumer, Webhook listener, Named Pipe).
2. **`IPolicyDataProvider`**: External ABAC attribute loader (e.g., query ERP user master table for dynamic approval limits).
3. **`IContextStorageProvider`**: Pluggable backend for large context handles (MemoryCache for desktop, Redis for web farm, Azure Blob for massive reports).
4. **`IAuditSink`**: Immutable audit logging sink (Elasticsearch, SQL Server ActionLog, Splunk).
