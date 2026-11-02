namespace Mani_App;

internal static class DiagnosticsBuild
{
    internal static bool Enabled =>
#if MANI_DIAGNOSTICS
        true;
#else
        false;
#endif
}
