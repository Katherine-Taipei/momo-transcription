using System;

namespace Momo.Infrastructure.Templates;

public interface ITemplateEngine
{
    string Render(string templateContent, object context);
}
