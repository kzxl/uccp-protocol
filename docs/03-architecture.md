# UCCP Architecture & System Topology

---

## 1. System Decomposition (C4 Level 2 - Container View)

The Universal Context & Capability Protocol (UCCP) is organized into modular, independently testable components. The architecture isolates the probabilistic reasoning of AI models from deterministic enterprise transactions.

```mermaid
flowchart TD
    subgraph ClientLayer ["Client & Perception Plane"]
        A1["AI Client / LLM Agent (Claude, GPT, Custom)"]
        A2["Desktop Application (WinForms, WPF)"]
        A3["Web Frontend / Mobile App"]
    end

    subgraph MediationLayer ["Mediation Plane (Protocol Gateway)"]
        B1["MCP Stdio/SSE Adapter"]
        B2["REST / OpenAPI 3.1 Adapter"]
        B3["gRPC Adapter"]
        B4["In-Process Direct Invoker"]
    end

    subgraph ControlPlane ["Control Plane (Governance & Context)"]
        C1["Capability Registry & Discovery Engine"]
        C2["Context Engine (Handle Store & State Tracker)"]
        C3["Policy Engine (ABAC & Permission Gate)"]
        C4["Audit & Observability Dispatcher"]
    end

    subgraph ExecutionPlane ["Execution Plane (Deterministic Runtime)"]
        D1["Execution Runtime (Dispatcher & Task Pool)"]
        D2["Workflow Engine (DAG & Saga Orchestrator)"]
        D3["Transaction & Idempotency Coordinator"]
    end

    subgraph EnterpriseBackends ["Persistence & Business Services"]
        E1["Enterprise App Services (MDS, Sales, Inventory)"]
        E2["Relational Database (SQL Server, PostgreSQL)"]
        E3["External APIs & Message Brokers"]
    end

    A1 -->|JSON-RPC via SSE/Stdio| B1
    A2 -->|In-Process Call| B4
    A3 -->|HTTP/REST| B2

    B1 -->|Unified Request Envelope| C1
    B2 -->|Unified Request Envelope| C1
    B3 -->|Unified Request Envelope| C1
    B4 -->|Direct C# Interface Call| C1

    C1 -->|Schema & Capability Match| C2
    C2 -->|Enriched Execution Context| C3
    C3 -->|Authorized Envelope| D1

    D1 -->|Direct Task| E1
    D1 -->|Composite Workflow| D2
    D2 -->|Coordinated Steps| E1

    D1 -->|Idempotency Verification| D3
    D1 -->|Structured Audit Event| C4

    E1 --> E2
    E1 --> E3
```

---

## 2. Component Responsibilities & Boundary Analysis

### 2.1. Protocol Gateway & Adapters
- **Function**: Ingests incoming requests from heterogeneous network transports (Stdio, SSE, HTTP, gRPC), parses them, and normalizes them into a canonical `UccpRequestEnvelope`.
- **Architectural Boundary**: Must be strictly stateless. Contains **zero** business logic and **zero** database knowledge.

### 2.2. Capability Registry & Discovery Engine
- **Function**: Maintains the catalog of all available enterprise capabilities, their semantic descriptions, hierarchical domain paths, and JSON Schemas.
- **Progressive Discovery**: Provides a 2-stage discovery mechanism:
  1. `DiscoverDomains(intent)`: Returns lightweight domain summaries.
  2. `ResolveCapability(capabilityId)`: Returns the detailed JSON Schema for the selected operation.

### 2.3. Context Engine
- **Function**: Manages the lifecycle, resolution, and isolation of execution context.
- **Context Handles**: Transforms large environmental states (e.g., 5,000-row inventory tables, user authorization tokens, tenant IDs) into opaque, immutable context handles (e.g., `ctx_inv_8f9a2b`).
- **Boundary Justification (Why separate from Policy Engine?)**:
  - The Context Engine manages **State and Data Access** (What is the truth of the environment?).
  - The Policy Engine evaluates **Rules and Decisions** (Is Subject $S$ permitted to perform Action $A$ on Resource $R$ given Context $C$?).
  - Conflating them tightly couples state storage with authorization logic, hindering independent testing and scaling.

### 2.4. Policy Engine (ABAC & Least Privilege)
- **Function**: Evaluates Attribute-Based Access Control (ABAC) policies at execution time.
- **Invariant**: The Policy Engine must intercept every request **after** context resolution and **before** runtime dispatch. Neither the LLM nor the Protocol Gateway can bypass this check.

### 2.5. Execution Runtime & Workflow Engine
- **Function**: The deterministic heart of UCCP.
  - **Direct Execution**: Dispatches atomic operations to underlying C# services or APIs.
  - **Workflow Execution**: Executes composite Directed Acyclic Graphs (DAGs) and Saga patterns (with automated rollbacks/compensations) without round-tripping intermediate steps through the LLM.

---

## 3. Two Deployment Profiles

To accommodate diverse enterprise environments—ranging from legacy on-premise Windows desktop monoliths to distributed cloud-native microservices—UCCP defines two standardized deployment profiles.

### Profile A: Minimal Architecture (In-Process Monolith / Embedded)
Designed for single-process desktop applications (WinForms/WPF), legacy .NET Framework 4.6.2 monoliths, and low-latency local execution.

```mermaid
graph LR
    subgraph SingleProcess ["Single Process Boundary (App.exe)"]
        UI["WinForms UI / AI Sidebar"]
        subgraph UCCP_Embedded ["UCCP Core Library (In-Process)"]
            Adapter["InProcessInvoker"]
            Registry["InMemoryRegistry"]
            Context["AmbientContextEngine"]
            Policy["LocalPolicyEngine"]
            Runtime["DirectRuntime"]
        end
        Services["Domain Services (MdsStockInAppService)"]
        DB[(SQL Server via EF6/Dapper)]
    end

    UI --> Adapter
    Adapter --> Registry --> Context --> Policy --> Runtime
    Runtime --> Services --> DB
```

- **Characteristics**:
  - Zero network serialization overhead ($< 0.5 \text{ ms}$ execution latency).
  - All inter-component calls use native C# interfaces (`IUccpRuntime`, `ICapabilityRegistry`).
  - Memory footprint: $< 15 \text{ MB}$ additional RAM.
  - Autofac or `IServiceProvider` handles dependency injection.

### Profile B: Distributed Architecture (Cloud / Microservices)
Designed for enterprise-scale multi-tenant environments with dozens of independent domain services.

```mermaid
graph TD
    Client["AI Gateway / Agent Hub"] -->|HTTPS / gRPC| PG["UCCP Edge Gateway"]
    
    subgraph ControlCluster ["UCCP Control Plane Cluster"]
        PG --> Redis[("Redis Context Cache & Registry")]
        PG --> PolicySvc["Policy Service (OPA / Cedar)"]
        PG --> Kafka[("Kafka Audit / Event Bus")]
    end

    subgraph WorkerPool ["UCCP Execution Workers"]
        PG --> Worker1["Execution Worker 1 (Temporal / Custom)"]
        PG --> Worker2["Execution Worker 2"]
    end

    Worker1 --> SvcInventory["Inventory Service"]
    Worker1 --> SvcPurchase["Purchase Service"]
    Worker2 --> SvcFinance["Finance Service"]
```

- **Characteristics**:
  - Horizontally scalable Gateway and Execution Workers.
  - Context handles stored in distributed cache (Redis) with TTL and encryption.
  - High-throughput asynchronous message bus (Kafka) for immutable audit trails.
  - Built-in circuit breakers, distributed tracing (OpenTelemetry / W3C Trace Context), and mutual TLS (mTLS).

---

## 4. End-to-End Execution Sequence

The following sequence illustrates a standard UCCP request traversing the system:

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant Agent as LLM Agent
    participant Gateway as UCCP Gateway
    participant Registry as Capability Registry
    participant ContextEng as Context Engine
    participant PolicyEng as Policy Engine
    participant Runtime as Execution Runtime
    participant Backend as Enterprise Service

    User->>Agent: "Check stock for RM-001 in Main Warehouse"
    Agent->>Gateway: Resolve Capability (Domain: "Inventory")
    Gateway->>Registry: Query Capability Metadata
    Registry-->>Agent: Returns `inventory.stock.check` Schema
    Agent->>Gateway: Execute(`inventory.stock.check`, { itemCode: "RM-001", whId: 1 })
    
    Gateway->>ContextEng: Resolve Context Handle (User, Tenant, Environment)
    ContextEng-->>Gateway: Enriched Execution Context
    
    Gateway->>PolicyEng: Authorize(User, Action: `inventory.stock.check`, Resource: "WH-1")
    alt Policy Denied
        PolicyEng-->>Gateway: Forbidden (403: Insufficient Permission)
        Gateway-->>Agent: Error Envelope (Access Denied)
    else Policy Allowed
        PolicyEng-->>Gateway: Authorized
        Gateway->>Runtime: Dispatch Execution
        Runtime->>Backend: Invoke Native Business Logic
        Backend-->>Runtime: Query Result (Data Rows)
        Runtime->>ContextEng: Store Full Data & Generate Compact Summary
        Runtime-->>Gateway: Result Envelope (Summary + Context Handle)
        Gateway-->>Agent: Response Envelope
        Agent-->>User: "RM-001 has 1,200 KG in Main Warehouse (Healthy)."
    end
```
