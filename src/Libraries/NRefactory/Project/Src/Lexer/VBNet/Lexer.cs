// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Andrea Paatz" email="andrea@icsharpcode.net"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

using ICSharpCode.NRefactory.Ast;
using ICSharpCode.NRefactory.Parser.VBNet.Experimental;

namespace ICSharpCode.NRefactory.Parser.VB
{
	internal sealed class Lexer : AbstractLexer
	{
		bool lineEnd = true;
		bool isAtLineBegin = false; // TODO: handle line begin, if neccessarry
		
		ExpressionFinder ef;
		
		public Lexer(TextReader reader) : base(reader)
		{
			ef = new ExpressionFinder();
		}
		
		public override Token NextToken()
		{
			if (curToken == null) { // first call of NextToken()
				curToken = Next();
				specialTracker.InformToken(curToken.kind);
				//Console.WriteLine("Tok:" + Tokens.GetTokenString(curToken.kind) + " --- " + curToken.val);
				return curToken;
			}
			
			lastToken = curToken;
			
			if (curToken.next == null) {
				curToken.next = Next();
				specialTracker.InformToken(curToken.next.kind);
			}
			
			curToken = curToken.next;
			
			if (curToken.kind == Tokens.EOF && !(lastToken.kind == Tokens.EOL)) { // be sure that before EOF there is an EOL token
				curToken = new Token(Tokens.EOL, curToken.col, curToken.line, string.Empty);
				specialTracker.InformToken(curToken.kind);
				curToken.next = new Token(Tokens.EOF, curToken.col, curToken.line, string.Empty);
				specialTracker.InformToken(curToken.next.kind);
			}
			//Console.WriteLine("Tok:" + Tokens.GetTokenString(curToken.kind) + " --- " + curToken.val);
			return curToken;
		}
		
		bool misreadExclamationMarkAsTypeCharacter;
		bool inXmlMode;
		
		class XmlModeStackInfo {
			public bool inXmlTag, inXmlCloseTag, wasComment, wasProcessingInstruction;
			public int level;
			
			public XmlModeStackInfo(bool isSpecial)
			{
				level = isSpecial ? -1 : 0;
				inXmlTag = inXmlCloseTag = wasComment = wasProcessingInstruction = false;
			}
		}
		
		Stack<XmlModeStackInfo> xmlModeStack = new Stack<XmlModeStackInfo>();
		
		Token NextInternal()
		{
			if (misreadExclamationMarkAsTypeCharacter) {
				misreadExclamationMarkAsTypeCharacter = false;
				return new Token(Tokens.ExclamationMark, Col - 1, Line);
			}
			
			unchecked {
				while (true) {
					Location startLocation = new Location(Col, Line);
					int nextChar = ReaderRead();
					if (nextChar == -1)
						return new Token(Tokens.EOF, Col, Line, string.Empty);
					char ch = (char)nextChar;
					#region XML mode
					if (inXmlMode && xmlModeStack.Peek().level <= 0 && !xmlModeStack.Peek().wasComment && !xmlModeStack.Peek().wasProcessingInstruction && !xmlModeStack.Peek().inXmlTag) {
						XmlModeStackInfo info = xmlModeStack.Peek();
						int peek;
						while (true) {
							int step = 0;
							while ((peek = ReaderPeek(step)) != -1 && XmlConvert.IsWhitespaceChar((char)peek))
								step++;

							if (ReaderPeek(step) == '<' && ReaderPeek(step + 1) == '!') {
								for (int i = 0; i < step + 2; i++)
									ReaderRead();
								
								Token token = ReadXmlCommentOrCData(Col - 1, Line);
								ReaderRead();
								return token;
							}
							
							break;
						}
						inXmlMode = false;
						xmlModeStack.Pop();
					}
					if (inXmlMode) {
						XmlModeStackInfo info = xmlModeStack.Peek();
						int x = Col - 1;
						int y = Line;
						switch (ch) {
							case '<':
								if (ReaderPeek() == '/') {
									ReaderRead();
									info.inXmlCloseTag = true;
									return new Token(Tokens.XmlOpenEndTag, x, y);
								}
								if (ReaderPeek() == '%' && ReaderPeek(1) == '=') {
									inXmlMode = false;
									ReaderRead(); ReaderRead();
									return new Token(Tokens.XmlStartInlineVB, x, y);
								}
								if (ReaderPeek() == '?') {
									ReaderRead();
									info.inXmlTag = true;
									return new Token(Tokens.XmlProcessingInstructionStart, x, y);
								}
								if (ReaderPeek() == '!') {
									ReaderRead();
									Token token = ReadXmlCommentOrCData(x, y);
									info.wasComment = token.Kind == Tokens.XmlComment;
									ReaderRead();
									return token;
								}
								info.level++;
								info.inXmlTag = true;
								return new Token(Tokens.XmlOpenTag, x, y);
							case '/':
								if (ReaderPeek() == '>') {
									ReaderRead();
									info.inXmlTag = false;
									info.level--;
									return new Token(Tokens.XmlCloseTagEmptyElement, x, y);
								}
								break;
							case '?':
								if (ReaderPeek() == '>') {
									ReaderRead();
									info.inXmlTag = false;
									info.wasProcessingInstruction = true;
									return new Token(Tokens.XmlProcessingInstructionEnd, x, y);
								}
								break;
							case '>':
								if (info.inXmlCloseTag)
									info.level--;
								info.wasComment = info.wasProcessingInstruction = false;
								info.inXmlTag = info.inXmlCloseTag = false;
								return new Token(Tokens.XmlCloseTag, x, y);
							case '=':
								return new Token(Tokens.Assign, x, y);
							case '\'':
							case '"':
								string s = ReadXmlString(ch);
								return new Token(Tokens.LiteralString, x, y, ch + s + ch, s, LiteralFormat.StringLiteral);
							default:
								if (info.inXmlCloseTag || info.inXmlTag) {
									if (XmlConvert.IsWhitespaceChar(ch))
										continue;
									return new Token(Tokens.Identifier, x, y, ReadXmlIdent(ch));
								} else {
									return new Token(Tokens.XmlContent, x, y, ReadXmlContent(ch));
								}
						}
						#endregion
					} else {
						#region Standard Mode
						if (Char.IsWhiteSpace(ch)) {
							if (HandleLineEnd(ch)) {
								if (lineEnd) {
									// second line end before getting to a token
									// -> here was a blank line
									specialTracker.AddEndOfLine(startLocation);
								} else {
									lineEnd = true;
									return new Token(Tokens.EOL, startLocation, new Location(Col, Line), null, null, LiteralFormat.None);
								}
							}
							continue;
						}
						if (ch == '_') {
							if (ReaderPeek() == -1) {
								errors.Error(Line, Col, String.Format("No EOF expected after _"));
								return new Token(Tokens.EOF, Col, Line, string.Empty);
							}
							if (!Char.IsWhiteSpace((char)ReaderPeek())) {
								int x = Col - 1;
								int y = Line;
								string s = ReadIdent('_');
								lineEnd = false;
								return new Token(Tokens.Identifier, x, y, s);
							}
							ch = (char)ReaderRead();
							
							bool oldLineEnd = lineEnd;
							lineEnd = false;
							while (Char.IsWhiteSpace(ch)) {
								if (HandleLineEnd(ch)) {
									lineEnd = true;
									break;
								}
								if (ReaderPeek() != -1) {
									ch = (char)ReaderRead();
								} else {
									errors.Error(Line, Col, String.Format("No EOF expected after _"));
									return new Token(Tokens.EOF, Col, Line, string.Empty);
								}
							}
							if (!lineEnd) {
								errors.Error(Line, Col, String.Format("Return expected"));
							}
							lineEnd = oldLineEnd;
							continue;
						}
						
						if (ch == '#') {
							while (Char.IsWhiteSpace((char)ReaderPeek())) {
								ReaderRead();
							}
							if (Char.IsDigit((char)ReaderPeek())) {
								int x = Col - 1;
								int y = Line;
								string s = ReadDate();
								DateTime time = new DateTime(1, 1, 1, 0, 0, 0);
								try {
									time = DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault);
								} catch (Exception e) {
									errors.Error(Line, Col, String.Format("Invalid date time {0}", e));
								}
								return new Token(Tokens.LiteralDate, x, y, s, time, LiteralFormat.DateTimeLiteral);
							} else {
								ReadPreprocessorDirective();
								continue;
							}
						}
						
						if (ch == '[') { // Identifier
							lineEnd = false;
							if (ReaderPeek() == -1) {
								errors.Error(Line, Col, String.Format("Identifier expected"));
							}
							ch = (char)ReaderRead();
							if (ch == ']' || Char.IsWhiteSpace(ch)) {
								errors.Error(Line, Col, String.Format("Identifier expected"));
							}
							int x = Col - 1;
							int y = Line;
							string s = ReadIdent(ch);
							if (ReaderPeek() == -1) {
								errors.Error(Line, Col, String.Format("']' expected"));
							}
							ch = (char)ReaderRead();
							if (!(ch == ']')) {
								errors.Error(Line, Col, String.Format("']' expected"));
							}
							return new Token(Tokens.Identifier, x, y, s);
						}
						if (Char.IsLetter(ch)) {
							int x = Col - 1;
							int y = Line;
							char typeCharacter;
							string s = ReadIdent(ch, out typeCharacter);
							if (typeCharacter == '\0') {
								int keyWordToken = Keywords.GetToken(s);
								if (keyWordToken >= 0) {
									// handle 'REM' comments
									if (keyWordToken == Tokens.Rem) {
										ReadComment();
										if (!lineEnd) {
											lineEnd = true;
											return new Token(Tokens.EOL, Col, Line, "\n");
										}
										continue;
									}
									
									lineEnd = false;
									return new Token(keyWordToken, x, y, s);
								}
							}
							
							lineEnd = false;
							return new Token(Tokens.Identifier, x, y, s);
							
						}
						if (Char.IsDigit(ch)) {
							lineEnd = false;
							return ReadDigit(ch, Col - 1);
						}
						if (ch == '&') {
							lineEnd = false;
							if (ReaderPeek() == -1) {
								return ReadOperator('&');
							}
							ch = (char)ReaderPeek();
							if (Char.ToUpper(ch, CultureInfo.InvariantCulture) == 'H' || Char.ToUpper(ch, CultureInfo.InvariantCulture) == 'O') {
								return ReadDigit('&', Col - 1);
							}
							return ReadOperator('&');
						}
						if (ch == '\'' || ch == '\u2018' || ch == '\u2019') {
							int x = Col - 1;
							int y = Line;
							ReadComment();
							if (!lineEnd) {
								lineEnd = true;
								return new Token(Tokens.EOL, x, y, "\n");
							}
							continue;
						}
						if (ch == '"') {
							lineEnd = false;
							int x = Col - 1;
							int y = Line;
							string s = ReadString();
							if (ReaderPeek() != -1 && (ReaderPeek() == 'C' || ReaderPeek() == 'c')) {
								ReaderRead();
								if (s.Length != 1) {
									errors.Error(Line, Col, String.Format("Chars can only have Length 1 "));
								}
								if (s.Length == 0) {
									s = "\0";
								}
								return new Token(Tokens.LiteralCharacter, x, y, '"' + s  + "\"C", s[0], LiteralFormat.CharLiteral);
							}
							return new Token(Tokens.LiteralString, x, y, '"' + s + '"', s, LiteralFormat.StringLiteral);
						}
						if (ch == '%' && ReaderPeek() == '>') {
							int x = Col - 1;
							int y = Line;
							inXmlMode = true;
							ReaderRead();
							return new Token(Tokens.XmlEndInlineVB, x, y);
						}
						#endregion
						if (ch == '<' && (ef.NextTokenIsPotentialStartOfXmlMode || ef.NextTokenIsStartOfImportsOrAccessExpression)) {
							xmlModeStack.Push(new XmlModeStackInfo(ef.NextTokenIsStartOfImportsOrAccessExpression));
							XmlModeStackInfo info = xmlModeStack.Peek();
							int x = Col - 1;
							int y = Line;
							inXmlMode = true;
							if (ReaderPeek() == '/') {
								ReaderRead();
								info.inXmlCloseTag = true;
								return new Token(Tokens.XmlOpenEndTag, x, y);
							}
							if (ReaderPeek() == '%' && ReaderPeek(1) == '=') {
								// TODO : suspend xml mode tracking
								ReaderRead(); ReaderRead();
								return new Token(Tokens.XmlStartInlineVB, x, y);
							}
							if (ReaderPeek() == '!') {
								ReaderRead();
								Token t = ReadXmlCommentOrCData(x, y);
								info.wasComment = t.Kind == Tokens.XmlComment;
								ReaderRead();
								return t;
							}
							if (ReaderPeek() == '?') {
								ReaderRead();
								info.inXmlTag = true;
								return new Token(Tokens.XmlProcessingInstructionStart, x, y);
							}
							info.inXmlTag = true;
							info.level++;
							return new Token(Tokens.XmlOpenTag, x, y);
						}
						Token token = ReadOperator(ch);
						if (token != null) {
							lineEnd = false;
							return token;
						}
					}
					
					errors.Error(Line, Col, String.Format("Unknown char({0}) which can't be read", ch));
				}
			}
		}
		
		Token ReadXmlCommentOrCData(int x, int y)
		{
			sb.Length = 0;
			int nextChar = -1;
			
			for (int i = 0; i < 7; i++) {
				nextChar = ReaderRead();
				if (nextChar > -1)
					sb.Append((char)nextChar);
			}
			
			if (sb.ToString().StartsWith("--")) {
				sb.Length = 0;
				while ((nextChar = ReaderRead()) != -1) {
					sb.Append((char)nextChar);
					if (ReaderPeek() == '>' && sb.ToString().EndsWith("--")) {
						string text = sb.Remove(sb.Length - 2, 2).ToString();
						return new Token(Tokens.XmlComment, x, y, text);
					}
				}
			}
			
			if (sb.ToString().StartsWith("[CDATA[")) {
				sb.Length = 0;
				while ((nextChar = ReaderRead()) != -1) {
					sb.Append((char)nextChar);
					if (ReaderPeek() == '>' && sb.ToString().EndsWith("]]")) {
						string text = sb.Remove(sb.Length - 2, 2).ToString();
						return new Token(Tokens.XmlCData, x, y, text);
					}
				}
			}
			
			return null;
		}
		
		string ReadXmlContent(char ch)
		{
			sb.Length = 0;
			while (true) {
				sb.Append(ch);
				int next = ReaderPeek();
				
				if (next == -1 || next == '<')
					break;
				ch = (char)ReaderRead();
			}
			
			return sb.ToString();
		}
		
		protected override Token Next()
		{
			Token t = NextInternal();
			ef.InformToken(t);
//			if (ef.Errors.Any()) {
//				foreach (Token token in ef.Errors) {
//					if (token != null)
//						errors.Error(token.Location.Line, token.Location.Column, "unexpected: " + token.ToString());
//				}
//			}
			ef.Advance();
			return t;
		}
		
		string ReadIdent(char ch)
		{
			char typeCharacter;
			return ReadIdent(ch, out typeCharacter);
		}
		
		string ReadIdent(char ch, out char typeCharacter)
		{
			typeCharacter = '\0';
			
			if (ef.ReadXmlIdentifier) {
				ef.ReadXmlIdentifier = false;
				return ReadXmlIdent(ch);
			}
			
			sb.Length = 0;
			sb.Append(ch);
			int peek;
			while ((peek = ReaderPeek()) != -1 && (Char.IsLetterOrDigit(ch = (char)peek) || ch == '_')) {
				ReaderRead();
				sb.Append(ch.ToString());
			}
			if (peek == -1) {
				return sb.ToString();
			}
			
			if ("%&@!#$".IndexOf((char)peek) != -1) {
				typeCharacter = (char)peek;
				ReaderRead();
				if (typeCharacter == '!') {
					peek = ReaderPeek();
					if (peek != -1 && (peek == '_' || peek == '[' || char.IsLetter((char)peek))) {
						misreadExclamationMarkAsTypeCharacter = true;
					}
				}
			}
			return sb.ToString();
		}
		
		string ReadXmlIdent(char ch)
		{
			sb.Length = 0;
			sb.Append(ch);
			
			int peek;
			
			while ((peek = ReaderPeek()) != -1 && (peek == ':' || XmlConvert.IsNCNameChar((char)peek))) {
				sb.Append((char)ReaderRead());
			}
			
			return sb.ToString();
		}
		
		char PeekUpperChar()
		{
			return Char.ToUpper((char)ReaderPeek(), CultureInfo.InvariantCulture);
		}
		
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1818:DoNotConcatenateStringsInsideLoops")]
		Token ReadDigit(char ch, int x)
		{
			sb.Length = 0;
			sb.Append(ch);
			
			int y = Line;
			string digit = "";
			if (ch != '&') {
				digit += ch;
			}
			
			bool ishex      = false;
			bool isokt      = false;
			bool issingle   = false;
			bool isdouble   = false;
			bool isdecimal  = false;
			
			if (ReaderPeek() == -1) {
				if (ch == '&') {
					errors.Error(Line, Col, String.Format("digit expected"));
				}
				return new Token(Tokens.LiteralInteger, x, y, sb.ToString() ,ch - '0', LiteralFormat.DecimalNumber);
			}
			if (ch == '.') {
				if (Char.IsDigit((char)ReaderPeek())) {
					isdouble = true; // double is default
					if (ishex || isokt) {
						errors.Error(Line, Col, String.Format("No hexadecimal or oktadecimal floating point values allowed"));
					}
					while (ReaderPeek() != -1 && Char.IsDigit((char)ReaderPeek())){ // read decimal digits beyond the dot
						digit += (char)ReaderRead();
					}
				}
			} else if (ch == '&' && PeekUpperChar() == 'H') {
				const string hex = "0123456789ABCDEF";
				sb.Append((char)ReaderRead()); // skip 'H'
				while (ReaderPeek() != -1 && hex.IndexOf(PeekUpperChar()) != -1) {
					ch = (char)ReaderRead();
					sb.Append(ch);
					digit += Char.ToUpper(ch, CultureInfo.InvariantCulture);
				}
				ishex = true;
			} else if (ReaderPeek() != -1 && ch == '&' && PeekUpperChar() == 'O') {
				const string okt = "01234567";
				sb.Append((char)ReaderRead()); // skip 'O'
				while (ReaderPeek() != -1 && okt.IndexOf(PeekUpperChar()) != -1) {
					ch = (char)ReaderRead();
					sb.Append(ch);
					digit += Char.ToUpper(ch, CultureInfo.InvariantCulture);
				}
				isokt = true;
			} else {
				while (ReaderPeek() != -1 && Char.IsDigit((char)ReaderPeek())) {
					ch = (char)ReaderRead();;
					digit += ch;
					sb.Append(ch);
				}
			}
			
			if (digit.Length == 0) {
				errors.Error(Line, Col, String.Format("digit expected"));
				return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), 0, LiteralFormat.DecimalNumber);
			}
			
			if (ReaderPeek() != -1 && "%&SILU".IndexOf(PeekUpperChar()) != -1 || ishex || isokt) {
				bool unsigned = false;
				if (ReaderPeek() != -1) {
					ch = (char)ReaderPeek();
					sb.Append(ch);
					ch = Char.ToUpper(ch, CultureInfo.InvariantCulture);
					unsigned = ch == 'U';
					if (unsigned) {
						ReaderRead(); // read the U
						ch = (char)ReaderPeek();
						sb.Append(ch);
						ch = Char.ToUpper(ch, CultureInfo.InvariantCulture);
						if (ch != 'I' && ch != 'L' && ch != 'S') {
							errors.Error(Line, Col, "Invalid type character: U" + ch);
						}
					}
				}
				try {
					if (isokt) {
						ReaderRead();
						ulong number = 0L;
						for (int i = 0; i < digit.Length; ++i) {
							number = number * 8 + digit[i] - '0';
						}
						if (ch == 'S') {
							if (unsigned)
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (ushort)number, LiteralFormat.OctalNumber);
							else
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (short)number, LiteralFormat.OctalNumber);
						} else if (ch == '%' || ch == 'I') {
							if (unsigned)
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (uint)number, LiteralFormat.OctalNumber);
							else
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (int)number, LiteralFormat.OctalNumber);
						} else if (ch == '&' || ch == 'L') {
							if (unsigned)
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (ulong)number, LiteralFormat.OctalNumber);
							else
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), (long)number, LiteralFormat.OctalNumber);
						} else {
							if (number > uint.MaxValue) {
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), unchecked((long)number), LiteralFormat.OctalNumber);
							} else {
								return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), unchecked((int)number), LiteralFormat.OctalNumber);
							}
						}
					}
					LiteralFormat literalFormat = ishex ? LiteralFormat.HexadecimalNumber : LiteralFormat.DecimalNumber;
					if (ch == 'S') {
						ReaderRead();
						if (unsigned)
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), UInt16.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
						else
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), Int16.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
					} else if (ch == '%' || ch == 'I') {
						ReaderRead();
						if (unsigned)
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), UInt32.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
						else
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), Int32.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
					} else if (ch == '&' || ch == 'L') {
						ReaderRead();
						if (unsigned)
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), UInt64.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
						else
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), Int64.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), literalFormat);
					} else if (ishex) {
						ulong number = UInt64.Parse(digit, NumberStyles.HexNumber);
						if (number > uint.MaxValue) {
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), unchecked((long)number), literalFormat);
						} else {
							return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), unchecked((int)number), literalFormat);
						}
					}
				} catch (OverflowException ex) {
					errors.Error(Line, Col, ex.Message);
					return new Token(Tokens.LiteralInteger, x, y, sb.ToString(), 0, LiteralFormat.None);
				}
			}
			Token nextToken = null; // if we accedently read a 'dot'
			if (!isdouble && ReaderPeek() == '.') { // read floating point number
				ReaderRead();
				if (ReaderPeek() != -1 && Char.IsDigit((char)ReaderPeek())) {
					isdouble = true; // double is default
					if (ishex || isokt) {
						errors.Error(Line, Col, String.Format("No hexadecimal or oktadecimal floating point values allowed"));
					}
					digit += '.';
					while (ReaderPeek() != -1 && Char.IsDigit((char)ReaderPeek())){ // read decimal digits beyond the dot
						digit += (char)ReaderRead();
					}
				} else {
					nextToken = new Token(Tokens.Dot, Col - 1, Line);
				}
			}
			
			if (ReaderPeek() != -1 && PeekUpperChar() == 'E') { // read exponent
				isdouble = true;
				digit +=  (char)ReaderRead();
				if (ReaderPeek() != -1 && (ReaderPeek() == '-' || ReaderPeek() == '+')) {
					digit += (char)ReaderRead();
				}
				while (ReaderPeek() != -1 && Char.IsDigit((char)ReaderPeek())) { // read exponent value
					digit += (char)ReaderRead();
				}
			}
			
			if (ReaderPeek() != -1) {
				switch (PeekUpperChar()) {
					case 'R':
					case '#':
						ReaderRead();
						isdouble = true;
						break;
					case 'D':
					case '@':
						ReaderRead();
						isdecimal = true;
						break;
					case 'F':
					case '!':
						ReaderRead();
						issingle = true;
						break;
				}
			}
			
			try {
				if (issingle) {
					return new Token(Tokens.LiteralSingle, x, y, sb.ToString(), Single.Parse(digit, CultureInfo.InvariantCulture), LiteralFormat.DecimalNumber);
				}
				if (isdecimal) {
					return new Token(Tokens.LiteralDecimal, x, y, sb.ToString(), Decimal.Parse(digit, NumberStyles.Currency | NumberStyles.AllowExponent, CultureInfo.InvariantCulture), LiteralFormat.DecimalNumber);
				}
				if (isdouble) {
					return new Token(Tokens.LiteralDouble, x, y, sb.ToString(), Double.Parse(digit, CultureInfo.InvariantCulture), LiteralFormat.DecimalNumber);
				}
			} catch (FormatException) {
				errors.Error(Line, Col, String.Format("{0} is not a parseable number", digit));
				if (issingle)
					return new Token(Tokens.LiteralSingle, x, y, sb.ToString(), 0f, LiteralFormat.DecimalNumber);
				if (isdecimal)
					return new Token(Tokens.LiteralDecimal, x, y, sb.ToString(), 0m, LiteralFormat.DecimalNumber);
				if (isdouble)
					return new Token(Tokens.LiteralDouble, x, y, sb.ToString(), 0.0, LiteralFormat.DecimalNumber);
			}
			Token token;
			try {
				token = new Token(Tokens.LiteralInteger, x, y, sb.ToString(), Int32.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), ishex ? LiteralFormat.HexadecimalNumber : LiteralFormat.DecimalNumber);
			} catch (Exception) {
				try {
					token = new Token(Tokens.LiteralInteger, x, y, sb.ToString(), Int64.Parse(digit, ishex ? NumberStyles.HexNumber : NumberStyles.Number), ishex ? LiteralFormat.HexadecimalNumber : LiteralFormat.DecimalNumber);
				} catch (FormatException) {
					errors.Error(Line, Col, String.Format("{0} is not a parseable number", digit));
					// fallback, when nothing helps :)
					token = new Token(Tokens.LiteralInteger, x, y, sb.ToString(), 0, LiteralFormat.DecimalNumber);
				} catch (OverflowException) {
					errors.Error(Line, Col, String.Format("{0} is too long for a integer literal", digit));
					// fallback, when nothing helps :)
					token = new Token(Tokens.LiteralInteger, x, y, sb.ToString(), 0, LiteralFormat.DecimalNumber);
				}
			}
			token.next = nextToken;
			return token;
		}
		
		void ReadPreprocessorDirective()
		{
			Location start = new Location(Col - 1, Line);
			string directive = ReadIdent('#');
			string argument  = ReadToEndOfLine();
			this.specialTracker.AddPreprocessingDirective(new PreprocessingDirective(directive, argument.Trim(), start, new Location(start.Column + directive.Length + argument.Length, start.Line)));
		}
		
		string ReadDate()
		{
			char ch = '\0';
			sb.Length = 0;
			int nextChar;
			while ((nextChar = ReaderRead()) != -1) {
				ch = (char)nextChar;
				if (ch == '#') {
					break;
				} else if (ch == '\n') {
					errors.Error(Line, Col, String.Format("No return allowed inside Date literal"));
				} else {
					sb.Append(ch);
				}
			}
			if (ch != '#') {
				errors.Error(Line, Col, String.Format("End of File reached before Date literal terminated"));
			}
			return sb.ToString();
		}
		
		string ReadString()
		{
			char ch = '\0';
			sb.Length = 0;
			int nextChar;
			while ((nextChar = ReaderRead()) != -1) {
				ch = (char)nextChar;
				if (ch == '"') {
					if (ReaderPeek() != -1 && ReaderPeek() == '"') {
						sb.Append('"');
						ReaderRead();
					} else {
						break;
					}
				} else if (ch == '\n') {
					errors.Error(Line, Col, String.Format("No return allowed inside String literal"));
				} else {
					sb.Append(ch);
				}
			}
			if (ch != '"') {
				errors.Error(Line, Col, String.Format("End of File reached before String terminated "));
			}
			return sb.ToString();
		}
		
		string ReadXmlString(char terminator)
		{
			char ch = '\0';
			sb.Length = 0;
			int nextChar;
			while ((nextChar = ReaderRead()) != -1) {
				ch = (char)nextChar;
				if (ch == terminator) {
					break;
				} else if (ch == '\n') {
					errors.Error(Line, Col, String.Format("No return allowed inside String literal"));
				} else {
					sb.Append(ch);
				}
			}
			if (ch != terminator) {
				errors.Error(Line, Col, String.Format("End of File reached before String terminated "));
			}
			return sb.ToString();
		}
		
		void ReadComment()
		{
			Location startPos = new Location(Col, Line);
			sb.Length = 0;
			StringBuilder curWord = specialCommentHash != null ? new StringBuilder() : null;
			int missingApostrophes = 2; // no. of ' missing until it is a documentation comment
			int nextChar;
			while ((nextChar = ReaderRead()) != -1) {
				char ch = (char)nextChar;
				
				if (HandleLineEnd(ch)) {
					break;
				}
				
				sb.Append(ch);
				
				if (missingApostrophes > 0) {
					if (ch == '\'' || ch == '\u2018' || ch == '\u2019') {
						if (--missingApostrophes == 0) {
							specialTracker.StartComment(CommentType.Documentation, isAtLineBegin, startPos);
							sb.Length = 0;
						}
					} else {
						specialTracker.StartComment(CommentType.SingleLine, isAtLineBegin, startPos);
						missingApostrophes = 0;
					}
				}
				
				if (specialCommentHash != null) {
					if (Char.IsLetter(ch)) {
						curWord.Append(ch);
					} else {
						string tag = curWord.ToString();
						curWord.Length = 0;
						if (specialCommentHash.ContainsKey(tag)) {
							Location p = new Location(Col, Line);
							string comment = ch + ReadToEndOfLine();
							this.TagComments.Add(new TagComment(tag, comment, isAtLineBegin, p, new Location(Col, Line)));
							sb.Append(comment);
							break;
						}
					}
				}
			}
			if (missingApostrophes > 0) {
				specialTracker.StartComment(CommentType.SingleLine, isAtLineBegin, startPos);
			}
			specialTracker.AddString(sb.ToString());
			specialTracker.FinishComment(new Location(Col, Line));
		}
		
		Token ReadOperator(char ch)
		{
			int x = Col - 1;
			int y = Line;
			switch(ch) {
				case '+':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.PlusAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.Plus, x, y);
				case '-':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.MinusAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.Minus, x, y);
				case '*':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.TimesAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.Times, x, y, "*");
				case '/':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.DivAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.Div, x, y);
				case '\\':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.DivIntegerAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.DivInteger, x, y);
				case '&':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.ConcatStringAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.ConcatString, x, y);
				case '^':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.PowerAssign, x, y);
						default:
							break;
					}
					return new Token(Tokens.Power, x, y);
				case ':':
					if (ReaderPeek() == '=') {
						ReaderRead();
						return new Token(Tokens.ColonAssign, x, y);
					}
					return new Token(Tokens.Colon, x, y);
				case '=':
					return new Token(Tokens.Assign, x, y);
				case '<':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.LessEqual, x, y);
						case '>':
							ReaderRead();
							return new Token(Tokens.NotEqual, x, y);
						case '<':
							ReaderRead();
							switch (ReaderPeek()) {
								case '=':
									ReaderRead();
									return new Token(Tokens.ShiftLeftAssign, x, y);
								default:
									break;
							}
							return new Token(Tokens.ShiftLeft, x, y);
					}
					return new Token(Tokens.LessThan, x, y);
				case '>':
					switch (ReaderPeek()) {
						case '=':
							ReaderRead();
							return new Token(Tokens.GreaterEqual, x, y);
						case '>':
							ReaderRead();
							if (ReaderPeek() != -1) {
								switch (ReaderPeek()) {
									case '=':
										ReaderRead();
										return new Token(Tokens.ShiftRightAssign, x, y);
									default:
										break;
								}
							}
							return new Token(Tokens.ShiftRight, x, y);
					}
					return new Token(Tokens.GreaterThan, x, y);
				case ',':
					return new Token(Tokens.Comma, x, y);
				case '.':
					// Prevent OverflowException when Peek returns -1
					int tmp = ReaderPeek(); int tmp2 = ReaderPeek(1);
					if (tmp > 0) {
						if (char.IsDigit((char)tmp))
							return ReadDigit('.', Col);
						else if ((char)tmp == '@') {
							ReaderRead();
							return new Token(Tokens.DotAt, x, y);
						} else if ((char)tmp == '.' && tmp2 > 0 && (char)tmp2 == '.') {
							ReaderRead(); ReaderRead();
							return new Token(Tokens.TripleDot, x, y);
						}
					}
					return new Token(Tokens.Dot, x, y);
				case '(':
					return new Token(Tokens.OpenParenthesis, x, y);
				case ')':
					return new Token(Tokens.CloseParenthesis, x, y);
				case '{':
					return new Token(Tokens.OpenCurlyBrace, x, y);
				case '}':
					return new Token(Tokens.CloseCurlyBrace, x, y);
				case '?':
					return new Token(Tokens.QuestionMark, x, y);
				case '!':
					return new Token(Tokens.ExclamationMark, x, y);
			}
			return null;
		}
		
		public override void SkipCurrentBlock(int targetToken)
		{
			int lastKind = -1;
			int kind = base.lastToken.kind;
			while (kind != Tokens.EOF &&
			       !(lastKind == Tokens.End && kind == targetToken))
			{
				lastKind = kind;
				NextToken();
				kind = lastToken.kind;
			}
		}
	}
}
