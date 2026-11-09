# UCCP Workflow Model & Capability Composition

---

## 1. The Anti-Pattern of LLM-Mediated Orchestration

In naive agent architectures, multi-step business transactions are coordinated iteratively through the LLM. 

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                      LLM-MEDIATED ORCHESTRATION FLAWS                       │
├──────────────────────────────────┬──────────────────────────────────────────┤
│ LLM-Mediated Orchestration (Bad) │ UCCP Workflow Composition (Optimal)      │
├──────────────────────────────────┼──────────────────────────────────────────┤
│ Step 1: LLM calls `stock.query`  │ LLM calls single composite capability:   │
│ Step 2: LLM receives 500 rows    │ `inventory.stock.check_reorder_workflow` │
│ Step 3: LLM calls `threshold.ev` │                                          │
│ Step 4: LLM calls `po.draft`     │ Execution Runtime executes entire DAG in │
│                                  │ C# / Database in $< 100 \text{ ms}$.      │
│ **5 Network Hops to LLM**        │ **1 Network Hop to LLM**                 │
│ **Total Latency: ~12–18 seconds**│ **Total Latency: ~1.2 seconds**          │
│ **Cost: ~15,000 prompt tokens**  │ **Cost: ~450 prompt tokens**             │
│ **Reliability: 74% (Drift risk)**│ **Reliability: 99.99% (Deterministic)**  │
└──────────────────────────────────┴──────────────────────────────────────────┘
```

---

## 2. Declarative DAG Workflow Specification

UCCP enables developers to define reusable, composite workflows composed of atomic capabilities:

```mermaid
flowchart TD
    Start([Start]) --> Step1["1. inventory.stock.query (Read stock balance)"]
    Step1 --> Step2{"2. Condition: Below Safety Threshold?"}
    
    Step2 -- No --> DoneHealthy([End: Stock Healthy])
    
    Step2 -- Yes --> ForkPoint{Parallel Fork}
    ForkPoint --> Step3A["3A. purchasing.history.query (Recent purchase prices)"]
    ForkPoint --> Step3B["3B. supplier.quotes.active (Active supplier bids)"]
    
    Step3A --> JoinPoint{Join & Aggregate}
    Step3B --> JoinPoint
    
    JoinPoint --> Step4["4. purchasing.po.draft (Create draft purchase order)"]
    Step4 --> CheckApproval{"Value > $5,000?"}
    
    CheckApproval -- Yes --> Step5A["5A. Suspended: Await Manager Approval"]
    CheckApproval -- No --> Step5B["5B. Auto-Approve & Dispatch"]
    
    Step5A --> End([End])
    Step5B --> End
```

---

## 3. Workflow Definition Contract (JSON Schema)

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "workflowId": "inventory.procurement.auto_reorder_flow@v1.0",
  "name": "Automated Stock Reorder & PO Generation Workflow",
  "type": "DAG",
  "timeoutMs": 30000,
  "steps": [
    {
      "stepId": "query_stock",
      "capabilityId": "inventory.material.stock.query@v1.0",
      "inputMapping": {
        "materialId": "$.workflowInput.materialId",
        "warehouseId": "$.workflowInput.warehouseId"
      }
    },
    {
      "stepId": "evaluate_threshold",
      "capabilityId": "inventory.planning.threshold.evaluate@v1.0",
      "dependsOn": ["query_stock"],
      "inputMapping": {
        "currentBalance": "$.steps.query_stock.output.availableQuantity",
        "minSafetyStock": "$.steps.query_stock.output.safetyStock"
      },
      "condition": "$.steps.query_stock.output.availableQuantity < $.steps.query_stock.output.safetyStock"
    },
    {
      "stepId": "fetch_quotes",
      "capabilityId": "purchasing.supplier.quotes.active@v1.0",
      "dependsOn": ["evaluate_threshold"],
      "inputMapping": {
        "materialId": "$.workflowInput.materialId"
      }
    },
    {
      "stepId": "create_draft_po",
      "capabilityId": "purchasing.po.create@v1.0",
      "dependsOn": ["fetch_quotes"],
      "inputMapping": {
        "materialId": "$.workflowInput.materialId",
        "supplierId": "$.steps.fetch_quotes.output.bestSupplierId",
        "quantity": "$.steps.evaluate_threshold.output.recommendedOrderQty",
        "unitPrice": "$.steps.fetch_quotes.output.lowestQuotedPrice"
      },
      "compensation": {
        "capabilityId": "purchasing.po.cancel@v1.0",
        "inputMapping": { "poId": "$.output.poId" }
      }
    }
  ]
}
```

---

## 4. Execution Engine Integration: In-Process vs. Durable

1. **Lightweight Monolith / Desktop (In-Process)**:
   - Implemented via a lightweight asynchronous DAG runner in C# (`UccpDagRunner`).
   - Uses `Task.WhenAll` for parallel branches and `CancellationTokenSource` for timeouts.
   - Zero external infrastructure dependency; runs natively in .NET Framework 4.6.2 and .NET 8.
2. **Distributed Cloud Enterprise (Durable Saga)**:
   - For long-running workflows requiring human sign-off across days, UCCP delegates to proven durable workflow engines (e.g., **Temporal**, **Elsa Workflows**, or **Azure Durable Functions**).
   - UCCP serves as the capability abstraction layer; the external engine manages state persistence and event resumption.
