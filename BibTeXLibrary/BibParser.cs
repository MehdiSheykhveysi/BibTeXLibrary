﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BibTeXLibrary
{
    using Next = Tuple<ParserState, BibBuilderState>;
    using Action = Dictionary<TokenType, Tuple<ParserState, BibBuilderState>>;
    using StateMap = Dictionary<ParserState, Dictionary<TokenType, Tuple<ParserState, BibBuilderState>>>;

    public sealed class BibParser : IDisposable
    {
        #region Const Field
        /// <summary>
        /// State tranfer map
        /// curState --Token--> (nextState, BibBuilderAction)
        /// </summary>
        private static readonly StateMap _stateMap = new StateMap
        {
            {ParserState.Begin,       new Action {
                { TokenType.Start,         new Next(ParserState.InStart,     BibBuilderState.Create) } } },
            {ParserState.InStart,     new Action {
                { TokenType.Name,          new Next(ParserState.InEntry,     BibBuilderState.SetType) } } },
            {ParserState.InEntry,     new Action {
                { TokenType.LeftBrace,     new Next(ParserState.InKey,       BibBuilderState.Skip) } } },
            {ParserState.InKey,       new Action {
                { TokenType.RightBrace,    new Next(ParserState.OutEntry,    BibBuilderState.Build) },
                { TokenType.Name,          new Next(ParserState.OutKey,      BibBuilderState.SetKey) },
                { TokenType.Comma,         new Next(ParserState.InTagName,   BibBuilderState.Skip) } } },
            {ParserState.OutKey,      new Action {
                { TokenType.Comma,         new Next(ParserState.InTagName,   BibBuilderState.Skip) } } },
            {ParserState.InTagName,   new Action {
                { TokenType.Name,          new Next(ParserState.InTagEqual,  BibBuilderState.SetTagName) },
                { TokenType.RightBrace,    new Next(ParserState.OutEntry,    BibBuilderState.Build) } } },
            {ParserState.InTagEqual,  new Action {
                { TokenType.Equal,         new Next(ParserState.InTagValue,  BibBuilderState.Skip) } } },
            {ParserState.InTagValue,  new Action {
                { TokenType.String,        new Next(ParserState.OutTagValue, BibBuilderState.SetTagValue) } } },
            {ParserState.OutTagValue, new Action {
                { TokenType.Concatenation, new Next(ParserState.InTagValue,  BibBuilderState.Skip) },
                { TokenType.Comma,         new Next(ParserState.InTagName,   BibBuilderState.SetTag) },
                { TokenType.RightBrace,    new Next(ParserState.OutEntry,    BibBuilderState.Build) } } },
            {ParserState.OutEntry,    new Action {
                { TokenType.Start,         new Next(ParserState.InStart,     BibBuilderState.Create) } } },
        }; 
        #endregion

        #region Private Field
        /// <summary>
        /// Input text stream.
        /// </summary>
        private readonly TextReader _inputText;

        /// <summary>
        /// Line No. counter.
        /// </summary>
        private int _lineCount = 1;

        /// <summary>
        /// Column counter.
        /// </summary>
        private int _colCount = 0;
        #endregion

        #region Constructor
        public BibParser(TextReader inputText)
        {
            _inputText = inputText;
        }
        #endregion

        #region Public Static Method
        /// <summary>
        /// Parse by given input text reader.
        /// </summary>
        /// <param name="inputText"></param>
        /// <returns></returns>
        public static List<BibEntry> Parse(TextReader inputText)
        {
            using (var parser = new BibParser(inputText))
            { 
                return parser.GetAllResult();
            }
        } 
        #endregion

        #region Public Method
        /// <summary>
        /// Get all result from Parser.
        /// </summary>
        /// <returns></returns>
        public List<BibEntry> GetAllResult()
        {
            var result = new List<BibEntry>();
            foreach (var entry in Parser())
            {
                result.Add(entry);
            }
            return result;
        }
        #endregion

        #region Private Method
        private IEnumerable<BibEntry> Parser()
        {
            var curState = ParserState.Begin;
            var nextState = ParserState.Begin;

            BibEntry bib = null;
            StringBuilder tagValueBuilder = new StringBuilder();
            string tagName = "";

            // Fetch token from Lexer and build BibEntry
            foreach (var token in Lexer())
            {
                // Transfer state
                if(_stateMap[curState].ContainsKey(token.Type))
                {
                    nextState = _stateMap[curState][token.Type].Item1;
                }
                else
                {
                    var expected = from pair in _stateMap[curState]
                                   select pair.Key;
                    throw new UnexpectedTokenException(_lineCount, _colCount, token.Type, expected.ToArray());
                }
                // Build BibEntry
                switch (_stateMap[curState][token.Type].Item2)
                {
                    case BibBuilderState.Create:
                        bib = new BibEntry();
                        break;

                    case BibBuilderState.SetType:
                        bib.Type = token.Value;
                        break;

                    case BibBuilderState.SetKey:
                        bib.Key = token.Value;
                        break;

                    case BibBuilderState.SetTagName:
                        tagName = token.Value;
                        break;

                    case BibBuilderState.SetTagValue:
                        tagValueBuilder.Append(token.Value);
                        break;

                    case BibBuilderState.SetTag:
                        bib[tagName] = tagValueBuilder.ToString();
                        tagValueBuilder.Clear();
                        tagName = string.Empty;
                        break;

                    case BibBuilderState.Build:
                        if(tagName != string.Empty)
                        {
                            bib[tagName] = tagValueBuilder.ToString();
                            tagValueBuilder.Clear();
                            tagName = string.Empty;
                        }
                        yield return bib;
                        break;
                }
                curState = nextState;
            }
            if(curState != ParserState.OutEntry)
            {
                var expected = from pair in _stateMap[curState]
                               select pair.Key;
                throw new UnexpectedTokenException(_lineCount, _colCount, TokenType.EOF, expected.ToArray());
            }
        }

        /// <summary>
        /// Lexer for BibTeX entry.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Token> Lexer()
        {
            int code;
            char c;
            int braceCount = 0;

            while ((code = Peek()) != -1)
            {
                c = (char)code;

                if (c == '@')
                {
                    yield return new Token(TokenType.Start);
                }
                else if (char.IsLetter(c))
                {
                    StringBuilder value = new StringBuilder();

                    while (true)
                    {
                        c = (char)Read();
                        value.Append(c);

                        if ((code = Peek()) == -1) break;
                        c = (char)code;

                        if (!char.IsLetterOrDigit(c) &&
                            c != '-' &&
                            c != '.' &&
                            c != '_') break;
                    }
                    yield return new Token(TokenType.Name, value.ToString());
                    goto ContinueExcute;
                }
                else if (char.IsDigit(c))
                {
                    StringBuilder value = new StringBuilder();

                    while (true)
                    {
                        c = (char)Read();
                        value.Append(c);

                        if ((code = Peek()) == -1) break;
                        c = (char)code;

                        if (!char.IsDigit(c)) break;
                    }
                    yield return new Token(TokenType.String, value.ToString());
                    goto ContinueExcute;
                }
                else if (c == '"')
                {
                    StringBuilder value = new StringBuilder();

                    _inputText.Read();
                    while ((code = Peek()) != -1)
                    {
                        if (c != '\\' && code == '"') break;

                        c = (char)Read();
                        value.Append(c);

                    }
                    yield return new Token(TokenType.String, value.ToString());
                }
                else if (c == '{')
                {
                    if (braceCount++ == 0)
                    {
                        yield return new Token(TokenType.LeftBrace);
                    }
                    else
                    {
                        StringBuilder value = new StringBuilder();
                        Read();
                        while (braceCount > 1 && (code = Peek()) != -1)
                        {
                            c = (char)Read();
                            if      (c == '{') braceCount++;
                            else if (c == '}') braceCount--;
                            if (braceCount > 1) value.Append(c);
                        }
                        yield return new Token(TokenType.String, value.ToString());
                        goto ContinueExcute;
                    }
                }
                else if (c == '}')
                {
                    braceCount--;
                    yield return new Token(TokenType.RightBrace);
                }
                else if (c == ',')
                {
                    yield return new Token(TokenType.Comma);
                }
                else if (c == '#')
                {
                    yield return new Token(TokenType.Concatenation);
                }
                else if (c == '=')
                {
                    yield return new Token(TokenType.Equal);
                }
                else if (c == '\n')
                {
                    _colCount = 0;
                    _lineCount++;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    throw new UnrecognizableCharacterException(_lineCount, _colCount, c);
                }

                // Move to next char if possible
                if (_inputText.Peek() != -1)
                    _inputText.Read();

                // Don't move
                ContinueExcute: continue;
            }
        }

        /// <summary>
        /// Peek next char but not move forward.
        /// </summary>
        /// <returns></returns>
        private int Peek()
        {
            return _inputText.Peek();
        }

        /// <summary>
        /// Read next char and move forward.
        /// </summary>
        /// <returns></returns>
        private int Read()
        {
            _colCount++;
            return _inputText.Read();
        }
        #endregion

        #region Impement Interface "IDisposable"
        /// <summary>
        /// Dispose stream resource.
        /// </summary>
        public void Dispose()
        {
            _inputText.Dispose();
        }
        #endregion
    }

    enum ParserState
    {
        Begin,
        InStart,
        InEntry,
        InKey,
        OutKey,
        InTagName,
        InTagEqual,
        InTagValue,
        OutTagValue,
        OutEntry
    }

    enum BibBuilderState
    {
        Create,
        SetType,
        SetKey,
        SetTagName,
        SetTagValue,
        SetTag,
        Build,
        Skip
    }
}
