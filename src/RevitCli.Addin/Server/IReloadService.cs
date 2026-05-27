using System.Threading.Tasks;

namespace RevitCli.Addin.Server;

public interface IReloadService
{
    Task<ReloadResponse> RequestReloadAsync();
}
