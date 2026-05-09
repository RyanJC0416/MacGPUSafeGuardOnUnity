using UnityEngine;
using UnityEngine.Rendering;
using EcoEngine.Rendering.Universal;
using Summer;

public class SetURPSettings : StepBase
{
    public override string Name => "SetUrpSettings";

    protected override bool OnEnter()
    {
#if UNITY_EDITOR
        if (xasset.Assets.NeedUpdateAllAssets)
        {
            SetUrpSettings();
        }
#else
        SetUrpSettings();
#endif
        return true;
    }

    protected override void OnExit()
    {
    }

    protected override bool OnUpdate()
    {
        return false;
    }

    private void SetUrpSettings()
    {
        Summer.Log.Info(LogTags.Framework, "SetUrpSettings start");
        var urp_settings_request = xasset.Asset.Load("Assets/Settings/urp.asset", typeof(UniversalRenderPipelineAsset));
        Summer.Log.Info(LogTags.Framework, "SetUrpSettings: load urp.asset end");

        if (urp_settings_request == null || !urp_settings_request.isDone)
        {
            return;
        }
        var urp_settings = urp_settings_request.asset as UniversalRenderPipelineAsset;
        if (urp_settings != null)
        {
            GraphicsSettings.renderPipelineAsset = urp_settings;
        }
        Summer.Log.Info(LogTags.Framework, "SetUrpSettings end");
    }
}
