namespace ConduitLLM.Tests.Gateway;

public sealed class Issue1657FixtureDiagnosticsTests
{
    [Fact]
    public void PrimaryAndCleanupDiagnosticsRetainOrderedTypesAndFramesWithoutPrivateValues()
    {
        const string secret = "private-issued-credential-and-provider-token";
        var primary = CapturePrimary(secret);
        var cleanup = CaptureCleanup(secret);
        primary.Data[secret] = secret;
        var combined = new AggregateException(secret, primary, cleanup);

        var diagnostics = NativeFixtureDiagnostics.Describe(combined);

        Assert.Contains("failure=0.0 type=System.InvalidOperationException", diagnostics);
        Assert.Contains("failure=0.1 type=System.IO.IOException", diagnostics);
        Assert.Contains(nameof(CapturePrimary), diagnostics);
        Assert.Contains(nameof(CaptureCleanup), diagnostics);
        Assert.Contains("hresult=0x", diagnostics);
        Assert.DoesNotContain(secret, diagnostics);
        Assert.Same(primary, combined.InnerExceptions[0]);
        Assert.Same(cleanup, combined.InnerExceptions[1]);
        Assert.Equal(diagnostics, NativeFixtureDiagnostics.Describe(combined));
    }

    [Fact]
    public void DiagnosticsDoNotReadExceptionMessagesAndBoundAggregateBreadth()
    {
        var exception = new AggregateException("private", Enumerable.Range(0, 100)
            .Select(_ => new UnreadableMessageException()));

        var diagnostics = NativeFixtureDiagnostics.Describe(exception);

        Assert.InRange(diagnostics.Split("failure=", StringSplitOptions.None).Length - 1, 1, 8);
        Assert.Contains("additional failures omitted", diagnostics);
        Assert.True(diagnostics.Length < 14000);
        Assert.DoesNotContain("private", diagnostics);
    }

    [Fact]
    public void FailureProjectionIncludesTheStageAndSafeCauseClassification()
    {
        var error = NativeFixtureDiagnostics.Failure("Owned credential removal failed.",
            new OperationCanceledException("private-token"));

        Assert.Contains("Owned credential removal failed.", error.Message);
        Assert.Contains(nameof(OperationCanceledException), error.Message);
        Assert.DoesNotContain("private-token", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ProgramStyleDescriptionOfAProjectionRetainsOriginalFramesAndClassification()
    {
        const string secret = "private-program-projection-token";
        var original = CaptureCleanup(secret);
        original.Data[secret] = secret;
        var projected = NativeFixtureDiagnostics.Failure("Admin cleanup; stage=token-removal.", original);

        var diagnostics = NativeFixtureDiagnostics.Describe(projected);

        Assert.Contains("context=Admin cleanup; stage=token-removal.", diagnostics);
        Assert.Contains("failure=0.0 type=System.IO.IOException", diagnostics);
        Assert.Contains("hresult=0x" + original.HResult.ToString("X8"), diagnostics);
        Assert.Contains(nameof(CaptureCleanup), diagnostics);
        Assert.DoesNotContain(secret, diagnostics);
        Assert.DoesNotContain(secret, projected.ToString());
        Assert.IsType<InvalidOperationException>(projected);
        Assert.Null(projected.InnerException);
    }

    [Fact]
    public void RepeatedPrimaryAndCleanupProjectionsRetainOriginalOrderWithinTheFailureBudget()
    {
        const string secret = "private-repeated-primary-and-cleanup-token";
        var primary = CapturePrimary(secret);
        var cleanup = CaptureCleanup(secret);
        var firstPrimary = NativeFixtureDiagnostics.Failure("Proof; stage=preflight.", primary);
        var firstCleanup = NativeFixtureDiagnostics.Failure("Cleanup; stage=redis-close.", cleanup);
        var secondPrimary = NativeFixtureDiagnostics.Failure("Terminal primary.", firstPrimary);
        var secondCleanup = NativeFixtureDiagnostics.Failure("Terminal cleanup.", firstCleanup);
        var combined = new AggregateException("Owned proof and cleanup failed.", secondPrimary, secondCleanup);

        var diagnostics = NativeFixtureDiagnostics.Describe(combined);

        const string originalPrimary = "failure=0.0.0.0 type=System.InvalidOperationException";
        const string originalCleanup = "failure=0.1.0.0 type=System.IO.IOException";
        Assert.Contains(originalPrimary, diagnostics);
        Assert.Contains(originalCleanup, diagnostics);
        Assert.True(diagnostics.IndexOf(originalPrimary, StringComparison.Ordinal) <
            diagnostics.IndexOf(originalCleanup, StringComparison.Ordinal));
        Assert.Contains(nameof(CapturePrimary), diagnostics);
        Assert.Contains(nameof(CaptureCleanup), diagnostics);
        Assert.Contains("context=Proof; stage=preflight.", diagnostics);
        Assert.Contains("context=Cleanup; stage=redis-close.", diagnostics);
        Assert.InRange(diagnostics.Split("failure=", StringSplitOptions.None).Length - 1, 1, 8);
        Assert.DoesNotContain(secret, diagnostics);
        Assert.DoesNotContain(secret, combined.ToString());
        Assert.Same(secondPrimary, combined.InnerExceptions[0]);
        Assert.Same(secondCleanup, combined.InnerExceptions[1]);
        Assert.All(new[] { firstPrimary, firstCleanup, secondPrimary, secondCleanup },
            error => Assert.Null(error.InnerException));
    }

    [Fact]
    public void DeepRepeatedProjectionRemainsBoundedWithoutReadingAnOriginalMessage()
    {
        Exception error = new UnreadableMessageException();
        for (var index = 0; index < 20; index++)
        {
            error = NativeFixtureDiagnostics.Failure("Closed fixture stage.", error);
        }

        var diagnostics = NativeFixtureDiagnostics.Describe(error);

        Assert.InRange(diagnostics.Split("failure=", StringSplitOptions.None).Length - 1, 1, 8);
        Assert.Contains("additional failures omitted", diagnostics);
        Assert.True(diagnostics.Length < 14000);
    }

    private static Exception CapturePrimary(string secret)
    {
        try { throw new InvalidOperationException(secret); }
        catch (InvalidOperationException exception) { return exception; }
    }

    private static Exception CaptureCleanup(string secret)
    {
        try { throw new IOException(secret); }
        catch (IOException exception) { return exception; }
    }

    private sealed class UnreadableMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Diagnostics must not read messages.");
    }
}
