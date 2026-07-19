using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Renders the managed Caddyfile from the routes state. Every value is
/// validated before rendering (see <see cref="CaddyRoutesStore.Validate"/>),
/// so no user input can introduce extra directives. Caddy obtains and renews
/// Let's Encrypt certificates automatically for each site block.
/// </summary>
public static class CaddyfileGenerator
{
    public static string Generate(CaddyRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Email.Length > 0)
        {
            CaddyRoutesStore.ValidateEmail(routes.Email);
        }

        var builder = new StringBuilder();
        builder.AppendLine("# Managed by pinqops — do not edit; changes are overwritten on apply.");
        if (routes.Email.Length > 0)
        {
            builder.AppendLine("{");
            builder.AppendLine($"\temail {routes.Email}");
            builder.AppendLine("}");
        }

        foreach (var route in routes.Routes)
        {
            CaddyRoutesStore.Validate(route);
            builder.AppendLine();
            builder.AppendLine($"{route.Domain} {{");
            builder.AppendLine($"\treverse_proxy {route.Target}:{route.Port}");
            builder.AppendLine("}");
        }

        return builder.ToString();
    }
}
