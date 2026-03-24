namespace OpenLightFX.Emby.Effects;

using Openlightfx;

public class EffectRendererFactory
{
    private readonly Dictionary<EffectType, IEffectRenderer> _renderers = new();

    public EffectRendererFactory()
    {
        Register(new Renderers.LightningRenderer());
        Register(new Renderers.FlameRenderer());
        Register(new Renderers.FlashbangRenderer());
        Register(new Renderers.ExplosionRenderer());
        Register(new Renderers.PulseRenderer());
        Register(new Renderers.StrobeRenderer());
        Register(new Renderers.SirenRenderer());
        Register(new Renderers.AuroraRenderer());
        Register(new Renderers.CandleRenderer());
        Register(new Renderers.GunfireRenderer());
        Register(new Renderers.NeonRenderer());
        Register(new Renderers.BreathingRenderer());
        Register(new Renderers.SparkRenderer());
    }

    public void Register(IEffectRenderer renderer)
    {
        _renderers[renderer.EffectType] = renderer;
    }

    public IEffectRenderer? GetRenderer(EffectType effectType)
    {
        return _renderers.TryGetValue(effectType, out var renderer) ? renderer : null;
    }

    public bool HasRenderer(EffectType effectType) => _renderers.ContainsKey(effectType);
}
