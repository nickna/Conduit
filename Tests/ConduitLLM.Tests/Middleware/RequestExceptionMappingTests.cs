using System.Net;

using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Core.Services;

using Microsoft.AspNetCore.Http;

using Moq;

namespace ConduitLLM.Tests.Middleware;

public class RequestExceptionMappingTests
{
    [Fact]
    public void Repeated_resolution_reuses_only_the_same_exception_and_translator()
    {
        var translator = new Mock<IProviderErrorTranslator>();
        translator.Setup(service => service.MapProviderError(It.IsAny<LLMCommunicationException>()))
            .Returns<LLMCommunicationException>(exception =>
                ExceptionToResponseMapper.MapProviderCommunicationStatus(exception.StatusCode, exception.Message));
        var context = new DefaultHttpContext();
        var first = new LLMCommunicationException("Rate limited", HttpStatusCode.TooManyRequests, null);
        var second = new LLMCommunicationException("Unavailable", HttpStatusCode.ServiceUnavailable, null);

        var mapping = RequestExceptionMapping.Resolve(context, first, translator.Object);
        Assert.Same(mapping, RequestExceptionMapping.Resolve(context, first, translator.Object));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable,
            RequestExceptionMapping.Resolve(context, second, translator.Object).StatusCode);

        translator.Verify(service => service.MapProviderError(first), Times.Once);
        translator.Verify(service => service.MapProviderError(second), Times.Once);
    }

    [Fact]
    public void Changing_translator_does_not_reuse_another_customer_modes_mapping()
    {
        var context = new DefaultHttpContext();
        var exception = new LLMCommunicationException("Provider rejected request", HttpStatusCode.Unauthorized, "Private text");
        var external = new ProviderErrorTranslator(new CustomerErrorOptions { Mode = CustomerErrorMode.External });
        var internalTranslator = new ProviderErrorTranslator(new CustomerErrorOptions { Mode = CustomerErrorMode.Internal });

        var externalMapping = RequestExceptionMapping.Resolve(context, exception, external);
        var internalMapping = RequestExceptionMapping.Resolve(context, exception, internalTranslator);

        Assert.Null(externalMapping.ProviderDetail);
        Assert.DoesNotContain("Private text", externalMapping.ResponseMessage);
        Assert.NotNull(internalMapping.ProviderDetail);
        Assert.Equal("Private text", internalMapping.ProviderDetail.RawMessage);
        Assert.Equal(externalMapping.StatusCode, internalMapping.StatusCode);
    }

    [Fact]
    public void Failed_translation_is_not_cached()
    {
        var context = new DefaultHttpContext();
        var exception = new LLMCommunicationException("Provider failure", HttpStatusCode.BadGateway, null);
        var mapping = ExceptionToResponseMapper.MapProviderCommunicationStatus(exception.StatusCode, exception.Message);
        var translator = new Mock<IProviderErrorTranslator>();
        translator.SetupSequence(service => service.MapProviderError(exception))
            .Throws(new IOException("Temporary translation failure"))
            .Returns(mapping);

        Assert.Throws<IOException>(() => RequestExceptionMapping.Resolve(context, exception, translator.Object));
        Assert.Same(mapping, RequestExceptionMapping.Resolve(context, exception, translator.Object));
        translator.Verify(service => service.MapProviderError(exception), Times.Exactly(2));
    }
}
