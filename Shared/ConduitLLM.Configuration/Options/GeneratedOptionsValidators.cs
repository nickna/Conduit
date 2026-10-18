using Microsoft.Extensions.Options;

namespace ConduitLLM.Configuration.Options;

// Compile the existing DataAnnotations contracts into bounded validators. Do not
// use ValidateDataAnnotations: its runtime property walk is incompatible with AOT.
[OptionsValidator]
public partial class BillingCostCanaryOptionsValidator : IValidateOptions<BillingCostCanaryOptions> { }

[OptionsValidator]
public partial class BillingReconciliationOptionsValidator : IValidateOptions<BillingReconciliationOptions> { }

[OptionsValidator]
public partial class BatchSpendingOptionsValidator : IValidateOptions<BatchSpendingOptions> { }

[OptionsValidator]
public partial class RedisCircuitBreakerOptionsValidator : IValidateOptions<RedisCircuitBreakerOptions> { }

[OptionsValidator]
public partial class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions> { }

[OptionsValidator]
public partial class SignalRConnectionOptionsValidator : IValidateOptions<SignalRConnectionOptions> { }
