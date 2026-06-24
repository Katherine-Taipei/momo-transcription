using Stubble.Core.Builders;

namespace Momo.Infrastructure.Templates;

public class StubbleTemplateEngine : ITemplateEngine
{
    private readonly Stubble.Core.Interfaces.IStubbleRenderer _renderer;

    public StubbleTemplateEngine()
    {
        _renderer = new StubbleBuilder().Build();
    }

    public string Render(string templateContent, object context)
    {
        if (string.IsNullOrEmpty(templateContent)) return string.Empty;
        return _renderer.Render(templateContent, context);
    }
}
