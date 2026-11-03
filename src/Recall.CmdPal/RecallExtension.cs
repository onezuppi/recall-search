using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;

namespace Recall.CmdPal;

[Guid("C803906B-3E37-45FE-A1E7-F152E4CF2F0C")]
public sealed partial class RecallExtension(ManualResetEvent disposed) : IExtension, IDisposable
{
    private readonly RecallCommandsProvider _provider = new();

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose() => disposed.Set();
}
