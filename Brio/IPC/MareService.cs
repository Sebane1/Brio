using Brio.Config;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;
using System.Linq;

namespace Brio.IPC;

public class McdfService : IDisposable
{
    public bool IsMcdfAvailable { get; private set; } = false;


    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ConfigurationService _configurationService;

    private readonly ICallGateSubscriber<string, IGameObject, bool> _McdfApplyMcdf;

    public McdfService(IDalamudPluginInterface pluginInterface, ConfigurationService configurationService)
    {
        _pluginInterface = pluginInterface;
        _configurationService = configurationService;

        _McdfApplyMcdf = pluginInterface.GetIpcSubscriber<string, IGameObject, bool>("McdfStandalone.LoadMcdf");

        RefreshMcdfStatus();

        _configurationService.OnConfigurationChanged += RefreshMcdfStatus;
    }

    public void RefreshMcdfStatus()
    {
        if(_configurationService.Configuration.IPC.AllowMcdfIntegration)
        {
            IsMcdfAvailable = ConnectToMcdf();
        }
        else
        {
            IsMcdfAvailable = false;
        }
    }

    public bool LoadMcdfAsync(string fileName, IGameObject target)
    {
        RefreshMcdfStatus();

        if(IsMcdfAvailable == false)
        {
            Brio.Log.Error($"Failed load MCDF file, Mcdf is not available");
            return false;
        }

        try
        {
            // Todo: Need a way to tell Mcdf that they can still sync other players?
            return _McdfApplyMcdf.InvokeFunc(fileName, target);
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, $"Failed to Invoke McdfSynchronos.LoadMcdfAsync IPC");
            return false;
        }
    }

    private bool ConnectToMcdf()
    {
        try
        {
            bool McdfInstalled = _pluginInterface.InstalledPlugins.Any(x => x.Name == "A Quest Reborn" && x.IsLoaded == true);

            if(!McdfInstalled)
            {
                Brio.Log.Debug("Mcdf Synchronos not present");
                return false;
            }

            Brio.Log.Debug("Mcdf Synchronos integration initialized");

            return true;
        }
        catch(Exception ex)
        {
            Brio.Log.Debug(ex, "Mcdf Synchronos initialize error");
            return false;
        }
    }

    public void Dispose()
    {
        _configurationService.OnConfigurationChanged -= RefreshMcdfStatus;
    }
}
