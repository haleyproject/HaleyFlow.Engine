using Xunit;

namespace WFE.Test.Fixtures;

public sealed class DatabaseFactAttribute : FactAttribute {
    public DatabaseFactAttribute() {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HALEYFLOW_TEST_CONNECTION")))
            Skip = "Set HALEYFLOW_TEST_CONNECTION to a disposable local MariaDB server.";
    }
}
