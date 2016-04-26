using System;
using System.Text;
using System.Text.RegularExpressions;

namespace i18n.Helpers
{
    using System.Collections.Generic;
    using System.Configuration;
    using System.Linq;
    using System.Web;
    using System.Web.UI;

    using i18n.Domain.Abstract;
    /// <summary>
    /// Helper class for locating and processing nuggets in a string.
    /// </summary>
    public class RecursiveNuggetParser: INuggetParser
    {
        private string m_delimiterWithParameterBeginToken;

        private string m_parameterEndBeforeDelimiterToken;

        private string m_parameterEndBeforeEndToken;

        private static readonly Dictionary<string, string> m_unescapeSqlTranslation = new Dictionary<string, string>
                                                                                {
                                                                                    { "%%", "%" },
                                                                                    { "''", "," }
                                                                                };

        private static readonly Dictionary<string, string> m_unescapeJavascriptTranslation = new Dictionary<string, string>
                                                                                       {
                                                                                            { "\\n", "\n" },
                                                                                            { "\n", "\r\n" },
                                                                                            { "\\t", "\t" },
                                                                                            { "\\\"", "\"" },
                                                                                            { "\\\\", "\\" },
                                                                                            { "\\'", "'" }
                                                                                       };
        private static readonly Dictionary<string, string> m_unescapeCSharpTranslation = new Dictionary<string, string>
                                                                        {
                                                                            { "\\n", "\n" },
                                                                            { "\n", "\r\n" },
                                                                            { "\\t", "\t" },
                                                                            { "\\\"", "\"" },
                                                                            { "\\\\", "\\" },
                                                                            { "\"\"", "\"" }
                                                                        };
        private static readonly Dictionary<string, string> m_escapeCSharpTranslation = new Dictionary<string, string>
                                                                      {
                                                                          { "\n", "\\n" },
                                                                          { "\r\n", "\n" },
                                                                          { "\t", "\\t" },
                                                                          { "\"", "\\\"" },
                                                                          { "\\", "\\\\" }
                                                                      };

        private static readonly Regex m_unescapeSqlRegex;

        private static readonly Regex m_unescapeJavascriptRegex;

        private static readonly Regex m_unescapeCSharpRegex;

        private static readonly Regex m_escapeCSharpRegex;

        static RecursiveNuggetParser()
        {
            var escapeRegexPatternRegex = new Regex("(.)", RegexOptions.Singleline);

            Func<Dictionary<string,string>, Regex> regexGenFunc = t =>
                new Regex(
                    string.Join(
                        "|",
                        t.Keys.Select(x => string.Format("({0})", escapeRegexPatternRegex.Replace(x, "\\$1")))
                            .OrderByDescending(x => x.Length)),
                    RegexOptions.Singleline | RegexOptions.Compiled);

            m_unescapeCSharpRegex = regexGenFunc(m_unescapeCSharpTranslation);
            m_unescapeJavascriptRegex = regexGenFunc(m_unescapeJavascriptTranslation);
            m_unescapeSqlRegex = regexGenFunc(m_unescapeSqlTranslation);
            m_escapeCSharpRegex = regexGenFunc(m_escapeCSharpTranslation);
        }

        /// <summary>
        /// Set during CON to nugget definition tokens.
        /// </summary>
        NuggetTokens m_nuggetTokens;

        /// <summary>
        /// Specifies whether the nugget is being parsed as part of source processing
        /// or response processing.
        /// </summary>
        NuggetParser.Context m_context;

        /// <summary>
        /// Initialized during CON to a regex suitable for breaking down a nugget into its component parts,
        /// as defined by the NuggetTokens definition passed to the CON.
        /// </summary>
        Regex m_tokensRegex;

        public RecursiveNuggetParser(
            NuggetTokens nuggetTokens,
            NuggetParser.Context context)
        {
            m_nuggetTokens = nuggetTokens;
            m_context = context;

            m_delimiterWithParameterBeginToken = nuggetTokens.DelimiterToken + nuggetTokens.ParameterBeginToken;
            m_parameterEndBeforeDelimiterToken = nuggetTokens.ParameterEndToken + nuggetTokens.DelimiterToken;
            m_parameterEndBeforeEndToken = nuggetTokens.ParameterEndToken + nuggetTokens.EndToken;

            // Prep the regexes. We escape each token char to ensure it is not misinterpreted.
            // · Breakdown e.g. "\[\[\[(.+?)(?:\|\|\|(.+?))*(?:\/\/\/(.+?))?\]\]\]"
            m_tokensRegex = new Regex(
                string.Format(@"(?:{1}{4})|(?:{5}{1})|(?:{5}{3})|(?:{0})|(?:{1})|(?:{2})|(?:{3})",
                    EscapeString(m_nuggetTokens.BeginToken),
                    EscapeString(m_nuggetTokens.DelimiterToken),
                    EscapeString(m_nuggetTokens.CommentToken),
                    EscapeString(m_nuggetTokens.EndToken),
                    EscapeString(m_nuggetTokens.ParameterBeginToken),
                    EscapeString(m_nuggetTokens.ParameterEndToken)),
                RegexOptions.CultureInvariant
                    | RegexOptions.Singleline);
            // RegexOptions.Singleline in fact enable multi-line nuggets.
        }

        // Operations

        /// <summary>
        /// Parses a string entity for nuggets, forwarding the nugget to a caller-provided
        /// delegate, with support for replacement of nugget strings in the entity.
        /// </summary>
        /// <param name="entity">
        /// String containing nuggets to be parsed. E.g. source code file, HTTP response entity.
        /// </param>
        /// <param name="ProcessNugget">
        /// Delegate callback to be called for each nugget encountered in entity:
        ///     delegate(string nuggetString, int pos, Nugget nugget1, string entity1).
        /// Returns a string with which to replace the nugget string in the source entity.
        /// If no change, then may return null.
        /// </param>
        /// <returns>
        /// Entity string reflecting any nugget strings replacements.
        /// </returns>
        public string ParseString(
            string entity,
            Func<string, int, Nugget, string, string> ProcessNugget, string fileExtension)
        {
            return new ParseOperation(this, entity, ProcessNugget, fileExtension).ParseAndProccess();
        }

        private class ParseOperation
        {
            private readonly RecursiveNuggetParser m_owner;

            private readonly string m_entity;

            private readonly Func<string, int, Nugget, string, string> m_processNugget;

            private readonly string m_fileExtension;

            public ParseOperation(
                RecursiveNuggetParser owner,
                string entity,
                Func<string, int, Nugget, string, string> processNugget,
                string fileExtension)
            {
                m_owner = owner;
                m_entity = entity;
                m_processNugget = processNugget;
                m_fileExtension = fileExtension;
            }

            public string ParseAndProccess()
            {
                var result = ParseAndProcessNuggetZone(0, false);

                return result.Replacement;
            }
            private ParseAndProccessResult ParseAndProcessNuggetZone(int position, bool isNested)
            {
                bool containsNuggets = false;

                StringBuilder processedZone = new StringBuilder();

                int nextPosition = position;
                for(;;)
                {
                    var match = m_owner.m_tokensRegex.Match(m_entity, nextPosition);
                    if (!match.Success)
                    {
                        processedZone.Append(
                            m_entity.Substring(nextPosition));
                        return new ParseAndProccessResult { Replacement = processedZone.ToString() };
                    }

                    if (match.Value == m_owner.m_nuggetTokens.BeginToken)
                    {
                        processedZone.Append(m_entity.Substring(nextPosition, match.Index - nextPosition));

                        var processedNugget = ParseAndProcessNugget(match.Index, isNested);

                        processedZone.Append(processedNugget.Replacement);
                        nextPosition = processedNugget.NextPosition;
                        containsNuggets = true;
                        continue;
                    }

                    if (isNested)
                    {
                        if (IsParameterEnd(match.Value))
                        {
                            if (!containsNuggets)
                            {
                                // This is original behavior of ((())).
                                var stringToProccess = m_entity.Substring(nextPosition, match.Index - nextPosition);
                                
                                var nugget = new Nugget
                                                 {
                                                     MsgId =
                                                         stringToProccess
                                                 };
                                string modifiedNuggetString = null;

                                if (m_owner.m_context == NuggetParser.Context.SourceProcessing)
                                {
                                    string fakeNuggetString = string.Format(
                                        "{0}{1}{2}",
                                        m_owner.m_nuggetTokens.BeginToken,
                                        stringToProccess,
                                        m_owner.m_nuggetTokens.EndToken);
                                    nugget.MsgId = PreProccessMsgId(nugget.MsgId);

                                    modifiedNuggetString = m_processNugget(
                                        fakeNuggetString, // entire nugget string
                                        m_owner.m_nuggetTokens.BeginToken.Length, // zero-based pos of the first char of entire nugget string
                                        nugget,                // broken-down nugget
                                        fakeNuggetString);               // source entity string
                                                                         // Returns either modified nugget string, or original nugget string (i.e. for no replacement).
                                }

                                return new ParseAndProccessResult
                                           {
                                               Replacement = modifiedNuggetString ?? stringToProccess,
                                               NextPosition = match.Index + m_owner.m_nuggetTokens.ParameterEndToken.Length,
                                               ContainNuggets = containsNuggets
                                           };
                            }

                            processedZone.Append(m_entity.Substring(nextPosition, match.Index - nextPosition));

                            return new ParseAndProccessResult
                            {
                                Replacement = processedZone.ToString(),
                                NextPosition = match.Index + m_owner.m_nuggetTokens.ParameterEndToken.Length,
                                ContainNuggets = containsNuggets
                            };
                        }
                    }

                    processedZone.Append(m_entity.Substring(nextPosition, match.Index + match.Length - nextPosition));
                    nextPosition = match.Index + match.Length;
                }
            }

            private string PreProccessMsgId(string msgId)
            {
                if (m_owner.m_context == NuggetParser.Context.SourceProcessing)
                {
                    string unescaped;
                    switch ((m_fileExtension ?? string.Empty).ToLower())
                    {
                        case ".sql":
                            unescaped = UnescapeSql(msgId);
                            return unescaped;
                        case ".cs":
                            unescaped = UnescapeCSharp(msgId);
                            return unescaped;
                        case ".js":
                            unescaped = UnescapeJavascript(msgId);
                            return unescaped;
                        case ".xml":
                        case ".html":
                        case ".resx":
                            unescaped = HttpUtility.HtmlDecode(msgId);
                            return unescaped;
                        default:
                            return msgId;
                    }

                }

                return msgId;
            }

            private string UnescapeCSharp(string str)
            {
                return ReplaceByPattern(m_unescapeCSharpTranslation, m_unescapeCSharpRegex, str);
            }

            private string UnescapeJavascript(string str)
            {
                return ReplaceByPattern(m_unescapeJavascriptTranslation, m_unescapeJavascriptRegex, str);
            }

            private string UnescapeSql(string str)
            {
                return ReplaceByPattern(m_unescapeSqlTranslation, m_unescapeSqlRegex, str);
            }

            private string EscapeCSharp(string str)
            {

                return ReplaceByPattern(m_escapeCSharpTranslation, m_escapeCSharpRegex, str);
            }

            private string ReplaceByPattern(Dictionary<string,string> translation, Regex regex, string str)
            {
                return regex.Replace(
                    str,
                    m =>
                        {
                            string replace;
                            if (translation.TryGetValue(m.Value, out replace))
                            {
                                return replace;
                            }

                            return m.Value;
                        });
            }

            /// <summary>
            /// Process nugget.
            /// </summary>
            /// <param name="position">Position right after begin token.</param>
            /// <returns>Parse result.</returns>
            private ParseAndProccessResult ParseAndProcessNugget(int position, bool isNested)
            {
                // Position of the comment block starting from token
                int? commentStartPosition = null;
                int? parameterStartPosition = null;

                bool wasNestedNugget = false;

                var nugget = new Nugget();
                var formatItems = new List<string>();

                int nextPosition = position + m_owner.m_nuggetTokens.BeginToken.Length;
                for (;;)
                {
                    var match = m_owner.m_tokensRegex.Match(m_entity, nextPosition);
                    if (!match.Success)
                    {
                        return new ParseAndProccessResult
                        {
                            Replacement = m_entity.Substring(position),
                            NextPosition = m_entity.Length
                        };
                    }

                    // Ignoring unexpected start of nugget.
                    if (match.Value == m_owner.m_nuggetTokens.BeginToken)
                    {
                        nextPosition = match.Index + match.Length;
                        continue;
                    }

                    // Processing unexected parameter end - )))
                    if (match.Value == m_owner.m_parameterEndBeforeDelimiterToken
                        || match.Value == m_owner.m_parameterEndBeforeEndToken)
                    {
                        if (isNested)
                        {
                            return new ParseAndProccessResult
                                       {
                                           Replacement =
                                               m_entity.Substring(
                                                   position,
                                                   match.Index - position),
                                           NextPosition = match.Index,
                                           ContainNuggets = true
                                       };
                        }

                        nextPosition = match.Index + m_owner.m_nuggetTokens.ParameterEndToken.Length;
                        continue;
                    }

                    // Skipping all tokens except NuggetEnd after comment was found.
                    if (commentStartPosition != null && match.Value != m_owner.m_nuggetTokens.EndToken)
                    {
                        nextPosition = match.Index + match.Length;
                        continue;
                    }

                    // Saving msgid and comment once parameter or end of nugget found.
                    if (nugget.MsgId == null)
                    {
                        nugget.MsgId = m_entity.Substring(
                            position + m_owner.m_nuggetTokens.BeginToken.Length,
                            match.Index - position - m_owner.m_nuggetTokens.BeginToken.Length);
                    }
                    
                    // Saving parameter start position
                    if (parameterStartPosition != null)
                    {
                        var formatItemString =
                            m_entity.Substring(
                                parameterStartPosition.Value + m_owner.m_nuggetTokens.DelimiterToken.Length,
                                match.Index - parameterStartPosition.Value
                                - m_owner.m_nuggetTokens.DelimiterToken.Length);
                        formatItems.Add(formatItemString);
                    }

                    // Processing parameter with nesting - |||(((
                    if (match.Value == m_owner.m_delimiterWithParameterBeginToken)
                    {
                        wasNestedNugget = true;
                        var result = ParseAndProcessNuggetZone(match.Index + match.Length, true);
                        nextPosition = result.NextPosition;
                        formatItems.Add(result.Replacement);
                        parameterStartPosition = null;
                        continue;
                    }

                    // Processing simple parameter
                    if (match.Value == m_owner.m_nuggetTokens.DelimiterToken)
                    {
                        parameterStartPosition = match.Index;
                        nextPosition = match.Index + match.Length;
                        continue;
                    }

                    // Saving comment start position
                    if (match.Value == m_owner.m_nuggetTokens.CommentToken)
                    {
                        if (commentStartPosition == null)
                        {
                            commentStartPosition = match.Index;
                        }

                        nextPosition = match.Index + match.Length;
                        continue;
                    }

                    if (match.Value == m_owner.m_nuggetTokens.EndToken)
                    {
                        if (commentStartPosition != null)
                        {
                            var commentTextStartPos = commentStartPosition.Value + m_owner.m_nuggetTokens.CommentToken.Length;
                            nugget.Comment = m_entity.Substring(commentTextStartPos, match.Index - commentTextStartPos);
                        }

                        if (nugget.MsgId != string.Empty)
                        {
                            var result = new ParseAndProccessResult { ContainNuggets = true, NextPosition = match.Index + match.Length};

                            if (formatItems.Any() && m_owner.m_context == NuggetParser.Context.ResponseProcessing)
                            {
                                nugget.FormatItems = formatItems.ToArray();
                            }

                            if (wasNestedNugget)
                            {
                                // Virtualization required
                                var virtualEntity = new StringBuilder();
                                virtualEntity.Append(m_owner.m_nuggetTokens.BeginToken);
                                virtualEntity.Append(nugget.MsgId);
                                if (nugget.Comment != null)
                                {
                                    virtualEntity.Append(m_owner.m_nuggetTokens.CommentToken);
                                    virtualEntity.Append(nugget.Comment);
                                }

                                foreach (var formatItem in formatItems)
                                {
                                    virtualEntity.Append(m_owner.m_nuggetTokens.DelimiterToken);
                                    virtualEntity.Append(formatItem);
                                }

                                virtualEntity.Append(m_owner.m_nuggetTokens.EndToken);
                                var virtualEntityString = virtualEntity.ToString();

                                // Processing nugget
                                var originalMsgId = nugget.MsgId;
                                nugget.MsgId = PreProccessMsgId(nugget.MsgId);
                                var replaceString = m_processNugget(virtualEntityString, 0, nugget, virtualEntityString);
                                result.Replacement = replaceString ?? originalMsgId;
                            }
                            else
                            {
                                // Processing nugget
                                var originalMsgId = nugget.MsgId;
                                nugget.MsgId = PreProccessMsgId(nugget.MsgId);

                                var replaceString =
                                    m_processNugget(
                                        m_entity.Substring(
                                            position,
                                            match.Index + match.Length - position),
                                        // entire nugget string
                                        position, // zero-based pos of the first char of entire nugget string
                                        nugget, // broken-down nugget
                                        m_entity); // source entity string

                                result.Replacement = replaceString ?? originalMsgId;
                            }

                            return result;
                        }

                        return new ParseAndProccessResult
                                   {
                                       Replacement = string.Empty,
                                       ContainNuggets = true,
                                       NextPosition = match.Index + match.Length
                                   };
                    }
                }
            }

            private bool IsParameterEnd(string str)
            {
                return str == (m_owner.m_nuggetTokens.ParameterEndToken + m_owner.m_nuggetTokens.EndToken)
                       || str == (m_owner.m_nuggetTokens.ParameterEndToken + m_owner.m_nuggetTokens.DelimiterToken);
            }
        }


        private class ParseAndProccessResult
        {
            public string Replacement { get; set; }
            public int NextPosition { get; set; }

            public bool ContainNuggets { get; set; }
        }


        /// <summary>
        /// Modifies a string such that each character is prefixed by another character
        /// (defaults to backslash).
        /// </summary>
        private static string EscapeString(string str, char escapeChar = '\\')
        {
            StringBuilder str1 = new StringBuilder(str.Length * 2);
            foreach (var c in str)
            {
                str1.Append(escapeChar);
                str1.Append(c);
            }
            return str1.ToString();
        }
    }
}
