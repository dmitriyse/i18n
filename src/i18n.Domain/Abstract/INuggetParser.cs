using System;

namespace i18n.Domain.Abstract
{
    using i18n.Helpers;

    public interface INuggetParser
    {
        string ParseString(string entity, Func<string, int, Nugget, string, string> func);
    }
}
