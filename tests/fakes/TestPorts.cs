using System;
// The loopback port a suite's fake server uses. run-tests.ps1 picks free ports at run time and passes each one as the
// environment variable CAWT_PORT_<n>, where <n> is the port the suite was written with. Run on its own, a suite falls back
// to that written port. Fake servers only ever bind 127.0.0.1.
static class TestPorts
{
    internal static int Get(int written)
    {
        string value = Environment.GetEnvironmentVariable("CAWT_PORT_" + written);
        int port;
        return value != null && int.TryParse(value, out port) && port > 0 && port < 65536 ? port : written;
    }

    // "http://127.0.0.1:<port>" with no trailing slash.
    internal static string Base(int written)
    {
        return "http://127.0.0.1:" + Get(written);
    }
}
