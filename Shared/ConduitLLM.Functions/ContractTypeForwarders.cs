using System.Runtime.CompilerServices;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Entities.Interfaces;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;

// Preserve binary references and assembly-qualified identities from before
// persistence contract ownership moved to the backend-neutral leaf.
[assembly: TypeForwardedTo(typeof(FunctionConfiguration))]
[assembly: TypeForwardedTo(typeof(FunctionCredential))]
[assembly: TypeForwardedTo(typeof(FunctionCost))]
[assembly: TypeForwardedTo(typeof(FunctionCostMapping))]
[assembly: TypeForwardedTo(typeof(FunctionExecution))]
[assembly: TypeForwardedTo(typeof(FunctionExecutionAudit))]
[assembly: TypeForwardedTo(typeof(FunctionCallAudit))]
[assembly: TypeForwardedTo(typeof(IIdentifiableEntity<>))]
[assembly: TypeForwardedTo(typeof(ICredentialEntity))]
[assembly: TypeForwardedTo(typeof(ExecutionMode))]
[assembly: TypeForwardedTo(typeof(ExecutionState))]
[assembly: TypeForwardedTo(typeof(FunctionAuditEventType))]
[assembly: TypeForwardedTo(typeof(FunctionCallAuditEventType))]
[assembly: TypeForwardedTo(typeof(FunctionPricingModel))]
[assembly: TypeForwardedTo(typeof(FunctionProviderType))]
[assembly: TypeForwardedTo(typeof(FunctionPurpose))]
[assembly: TypeForwardedTo(typeof(IAuditEvent))]
[assembly: TypeForwardedTo(typeof(IFunctionCallAuditService))]
[assembly: TypeForwardedTo(typeof(FunctionCallAuditSummary))]
[assembly: TypeForwardedTo(typeof(IFunctionConfigurationRepository))]
[assembly: TypeForwardedTo(typeof(IFunctionCredentialRepository))]
[assembly: TypeForwardedTo(typeof(IFunctionCostRepository))]
[assembly: TypeForwardedTo(typeof(IFunctionCostMappingRepository))]
[assembly: TypeForwardedTo(typeof(IFunctionExecutionRepository))]
