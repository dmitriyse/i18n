using System;
using System.Text;
using System.Text.RegularExpressions;

namespace i18n.Helpers
{
    using System.Collections.Generic;
    using System.Linq;

    using i18n.Domain.Abstract;
    /// <summary>
    /// Helper class for locating and processing nuggets in a string.
    /// </summary>
    public class RecursiveNuggetParser: INuggetParser
    {
        private string m_delimiterWithParameterBeginToken;

        private string m_parameterEndBeforeDelimiterToken;

        private string m_parameterEndBeforeEndToken;

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
            Func<string, int, Nugget, string, string> ProcessNugget)
        {
            return new ParseOperation(this, entity, ProcessNugget).ParseAndProccess();
        }


        /// <summary>
        /// Parses a nugget string to breakdown the nugget into individual components.
        /// </summary>
        /// <param name="nugget">Subject nugget string.</param>
        /// <returns>If successful, returns Nugget instance; otherwise returns null indicating a badly formatted nugget string.</returns>
        public Nugget BreakdownNugget(string nugget)
        {
            Match match = m_tokensRegex.Match(nugget);
            return NuggetFromRegexMatch(match);
        }

        private class ParseOperation
        {
            private readonly RecursiveNuggetParser m_owner;

            private readonly string m_entity;

            private readonly StringBuilder m_result;

            private readonly Func<string, int, Nugget, string, string> m_processNugget;

            public ParseOperation(RecursiveNuggetParser owner, string entity, Func<string, int, Nugget, string, string> processNugget)
            {
                m_owner = owner;
                m_entity = entity;
                m_result = new StringBuilder();
                m_processNugget = processNugget;
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

                                string fakeNuggetString = string.Format(
                                    "{0}{1}{2}",
                                    m_owner.m_nuggetTokens.BeginToken,
                                    stringToProccess,
                                    m_owner.m_nuggetTokens.EndToken);

                                string modifiedNuggetString = m_processNugget(
                                    fakeNuggetString, // entire nugget string
                                    m_owner.m_nuggetTokens.BeginToken.Length, // zero-based pos of the first char of entire nugget string
                                    nugget,                // broken-down nugget
                                    fakeNuggetString);               // source entity string
                                                           // Returns either modified nugget string, or original nugget string (i.e. for no replacement).
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
                }
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

                            if (formatItems.Any())
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
                                var replaceString = m_processNugget(virtualEntityString, 0, nugget, virtualEntityString);
                                result.Replacement = replaceString ?? nugget.MsgId;
                            }
                            else
                            {
                                // Processing nugget
                                var replaceString =
                                    m_processNugget(
                                        m_entity.Substring(
                                            position,
                                            match.Index + match.Length - position),
                                        // entire nugget string
                                        position, // zero-based pos of the first char of entire nugget string
                                        nugget, // broken-down nugget
                                        m_entity); // source entity string

                                result.Replacement = replaceString ?? nugget.MsgId;
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

        /// <summary>
        /// Returns a nugget instance loaded from a regex match, or null if error.
        /// </summary>
        private Nugget NuggetFromRegexMatch(Match match)
        {
            if (!match.Success
                || match.Groups.Count != 4)
            {
                return null;
            }
            Nugget n = new Nugget();
            // Extract msgid from 2nd capture group.
            n.MsgId = match.Groups[1].Value;
            // Extract format items from 3rd capture group.
            var formatItems = match.Groups[2].Captures;
            if (formatItems.Count != 0)
            {
                n.FormatItems = new string[formatItems.Count];
                int i = 0;
                foreach (Capture capture in formatItems)
                {
                    if (m_context == NuggetParser.Context.SourceProcessing
                        && !capture.Value.IsSet())
                    {
                        return null;
                    } // bad format
                    n.FormatItems[i++] = capture.Value;
                }
            }
            // Extract comment from 4th capture group.
            if (match.Groups[3].Value.IsSet())
            {
                n.Comment = match.Groups[3].Value;
            }
            // Success.
            return n;
        }
    }
}
