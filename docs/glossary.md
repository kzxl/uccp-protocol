# UCCP Architectural Glossary & Domain Taxonomy

---

### ABAC (Attribute-Based Access Control)
An authorization paradigm where access rights are granted to users through policies that combine attributes (user role, department, resource classification, action risk, environmental time).

### Action
The fundamental classification of state effect performed by a capability: `READ` (zero mutation), `WRITE` (entity mutation), `EXECUTE` (triggering business logic), `ADMIN` (security/system changes).

### Capability
A self-contained, versioned enterprise business contract that encapsulates intent, preconditions, schema contracts, policy governance, and deterministic execution logic to achieve a business outcome.

### Capability Descriptor
A machine-readable JSON Schema document that formally defines the identity, inputs, outputs, security constraints, and metadata of a Capability.

### Cognitive Duality
The architectural principle separating probabilistic, fuzzy AI reasoning (LLM perception plane) from deterministic, rule-based execution (backend runtime plane).

### Compensation (Saga Pattern)
A compensating transaction executed by the Workflow Engine to undo the observable effects of a previously completed step when a subsequent step in a distributed transaction fails.

### Confused Deputy Problem
A security vulnerability where an AI agent with high system privileges is manipulated by an unprivileged user's prompt into executing an action on their behalf that the user lacks permission to perform.

### Context Engine
The subsystem responsible for storing, resolving, isolating, and propagating the 7 tiers of enterprise context (Identity, Business, Session, Conversation, Execution, Environment, Data).

### Context Handle
An opaque, cryptographically signed reference (e.g., `ctx_hnd_89a01bf2`) pointing to a large or sensitive operational dataset in the Context Store, allowing an LLM to refer to data without loading it into prompt tokens.

### Direct Execution
A point-to-point execution pattern where a single atomic capability is dispatched directly to an application service or API without intermediate workflow coordination.

### Domain
A bounded business area within an enterprise (e.g., `Inventory`, `Purchasing`, `Finance`, `Sales`, `HumanResources`).

### Idempotency Key
A unique UUID provided by the caller ensuring that an identical state-modifying request can be retried across network failures without executing the underlying business transaction more than once.

### Indirect Prompt Injection
An attack where untrusted data processed by an AI agent (e.g., supplier invoice notes, customer email body) contains malicious instructions designed to hijack the model's instruction flow.

### Level 3 Empirical Proof (`R_PROOF`)
Verification evidence derived from independent deterministic sources (e.g., compiler exit code 0, automated test assertion counts, BenchmarkDotNet logs) rather than subjective self-assessment.

### MCP (Model Context Protocol)
An open standard introduced by Anthropic for connecting AI models to tools, resources, and prompt templates over JSON-RPC 2.0 (primarily using Stdio and SSE transports).

### Opaque Reference
A token or pointer whose internal byte contents are not directly readable or manipulable by the client, ensuring data hiding and security encapsulation.

### Policy Decision Point (PDP)
The component (Policy Engine) that evaluates applicable ABAC rules and issues an authorization decision (`ALLOW`, `DENY`, `REQUIRE_APPROVAL`).

### Policy Enforcement Point (PEP)
The component (UCCP Gateway / Dispatcher) that intercepts execution requests and enforces the PDP's decision before code is invoked.

### Progressive Disclosure
A 3-stage discovery model (Domain Signatures $\to$ Capability Candidates $\to$ Lazy Schema Hydration) that prevents token window bloat by loading schemas on-demand.

### Protocol Adapter
A translation layer that maps an external wire protocol (MCP, REST/OpenAPI, gRPC, WebSocket) into canonical UCCP Request and Response Envelopes.

### UCCP (Universal Context & Capability Protocol)
A unified enterprise semantic architecture and execution standard designed to connect AI perception models to enterprise systems securely, deterministically, and efficiently.
