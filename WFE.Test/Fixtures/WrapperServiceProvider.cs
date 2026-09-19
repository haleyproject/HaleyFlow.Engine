namespace WFE.Test.Fixtures;

internal sealed class WrapperServiceProvider(Func<ContinuationWrapper> create) : IServiceProvider {
    public object? GetService(Type serviceType) => serviceType == typeof(ContinuationWrapper) ? create() : null;
}
