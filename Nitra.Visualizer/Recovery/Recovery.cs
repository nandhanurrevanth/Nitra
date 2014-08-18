﻿//#region Пролог
//#define DebugOutput
//#define DebugThreading

using System.Text;
using Nemerle.Collections;
using Nitra.Internal.Recovery;
using Nitra.Runtime.Errors;
using Nitra.Runtime.Reflection;

using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

using NB = Nemerle.Builtins;
using SCG = System.Collections.Generic;

//using ParsedSequenceAndSubrule2 = Nemerle.Builtins.Tuple</*Inserted tokens*/int, Nitra.Internal.Recovery.ParsedSequence, Nitra.Internal.Recovery.ParsedSubrule>;

#if NITRA_RUNTIME
namespace Nitra.Strategies
#else
// ReSharper disable once CheckNamespace

using Nemerle.Core;

namespace Nitra.DebugStrategies
#endif
{
  using SubrulesTokenChanges = Dictionary<ParsedSubrule, TokenChanges>;
  using ParsedSequenceAndSubrules = Nemerle.Core.list<SubruleTokenChanges>;
  using FlattenSequences = List<Nemerle.Core.list<SubruleTokenChanges>>;
  using ParsedList = Nemerle.Core.list<ParsedSequenceAndSubrule>;
  using Nitra.Runtime;

  //#endregion

  public static class RecoveryDebug
  {
    public static string CurrentTestName;
  }

  public class Recovery
  {
    public const int NumberOfTokensForSpeculativeDeleting = 4;
    public const int Fail = int.MaxValue;
    private readonly Dictionary<ParsedSequenceAndSubrule, bool> _deletedToken = new Dictionary<ParsedSequenceAndSubrule, bool>();
    private ParseResult _parseResult;
    private RecoveryParser _recoveryParser;
#if DebugThreading
    private static int _counter = 0;
    private int _id;
    public int ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

    public Recovery()
    {
      _id = System.Threading.Interlocked.Increment(ref _counter);
      Debug.WriteLine("Recovery() " + _id + " ThreadId=" + System.Threading.Thread.CurrentThread.ManagedThreadId);
    }
#endif

    public virtual int Strategy(ParseResult parseResult)
    {
#if DebugThreading
      if (ThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
        Debug.Assert(false);
      Debug.WriteLine(">>>> Strategy " + _id + " ThreadId=" + System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif
      //Debug.Assert(parseResult.RecoveryStacks.Count > 0);
      _callerInfoMap = new Dictionary<ParsingCallerInfo, ParsingCallerInfo>();
      _parseResult = parseResult;

#if DebugOutput
      Debug.IndentSize = 1;
      var timer = Stopwatch.StartNew();
      Debug.WriteLine(RecoveryDebug.CurrentTestName + " -----------------------------------------------------------");
#endif
      _deletedToken.Clear();
      //var textLen = parseResult.Text.Length;
      var rp = new RecoveryParser(parseResult);
      _recoveryParser = rp;
      rp.StartParse(parseResult.RuleParser);//, parseResult.MaxFailPos);
      var startSeq = rp.StartSequence;

      UpdateEarleyParseTime();
#if DebugOutput
      timer.Stop();
      Debug.WriteLine("Earley parse took: " + timer.Elapsed);
      timer.Restart();
#endif

      RecoverAllWays(rp);

      UpdateRecoverAllWaysTime();
#if DebugOutput
      timer.Stop();
      Debug.WriteLine("RecoverAllWays took: " + timer.Elapsed);
      timer.Restart();
#endif

      if (parseResult.TerminateParsing)
        throw new OperationCanceledException();

      UpdateFindBestPathTime();
#if DebugOutput
      timer.Stop();
      Debug.WriteLine("FindBestPath took: " + timer.Elapsed);
      timer.Restart();
#endif

      if (parseResult.TerminateParsing)
        throw new OperationCanceledException();

      //var results = FlattenSequence(new FlattenSequences() { Nemerle.Collections.NList.ToList(new SubruleTokenChanges[0]) },
      //  parseResult, startSeq, textLen, memiozation[new ParsedSequenceKey(startSeq, textLen)].TotalTokenChanges, memiozation);

      //ParsePathsVisializer.PrintPaths(parseResult, _deletedToken, results);

      if (parseResult.TerminateParsing)
        throw new OperationCanceledException();

      UpdateFlattenSequenceTime();
#if DebugOutput
      timer.Stop();
      Debug.WriteLine("FlattenSequence took: " + timer.Elapsed);
#endif

#if DebugThreading
      Debug.WriteLine("<<<< Strategy " + _id + " ThreadId=" + System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif

      if (parseResult.TerminateParsing)
        throw new OperationCanceledException();

      //AstPatcher3.PatchAst(startSeq, rp, _deletedToken);
      var patcer = new AstPatcher4(startSeq, rp, _deletedToken);
      patcer.PatchAst();
      //patcer.Visualize();

      CollectError(parseResult);

      _parseResult = null;

      return parseResult.Text.Length;
    }

    private static void CollectError(ParseResult parseResult)
    {
      var errorCollector = new ErrorCollectorWalker();
      errorCollector.Walk(parseResult);
    }

    private List<Tuple<int, ParsedSequence>> FindMaxFailPos(RecoveryParser rp)
    {
      // В следстии особенностей работы основного парсере некоторые правила могут с
      var result = new List<Tuple<int, ParsedSequence>>(3);
      int maxPos;
      do
      {
        maxPos = rp.MaxPos;
        int count;
        do
        {
          var records = rp.Records[maxPos].ToArray(); // to materialize collection

          // Среди текущих состояний могут быть эски. Находим их и засовываем их кишки в Эрли.
          foreach (var record in records)
            if (record.State >= 0)
            {
              var state = record.ParsingState;
              if (state.IsToken)
              {
                var simple = state as ParsingState.Simple;
                if (simple == null || !simple.RuleParser.Descriptor.Name.Equals("s", StringComparison.InvariantCultureIgnoreCase))
                  continue;
                rp.PredictionOrScanning(maxPos, record, false);
              }
            }

          count = records.Length;
          var sequences = GetSequences(rp, maxPos).ToArray();
          foreach (var sequence in sequences)
          {
            if (sequence.IsToken)
            {
              // если последовательность - это эска, пробуем удалить за ней грязь или добавить ее в result для дальнешей попытки удаления токенов.
              if (sequence.ParsingSequence.RuleName == "s")
              {
                if (TryDeleteGarbage(rp, maxPos, sequence))
                  continue;
                result.Add(Tuple.Create(maxPos, sequence));
                continue;
              }

              if (sequence.ParsingSequence.RuleName != "S")
                continue;
            }
            // Если в последовательнсости есть пропарсивания оканчивающиеся на место падения, добавляем кишки этого состояния в Эрли.
            // Это позволит, на следующем шаге, поискать в них эски.
            foreach (var subrule in sequence.ParsedSubrules.ToArray())
              if (subrule.State >= 0 && subrule.End == maxPos && sequence.ParsingSequence.SequenceInfo != null)
              {
                var state = sequence.ParsingSequence.States[subrule.State];
                if (state.IsToken)
                {
                  var simple = state as ParsingState.Simple;
                  if (simple == null || simple.RuleParser.Descriptor.Name != "S" && simple.RuleParser.Descriptor.Name != "s")
                    continue;
                }
                rp.PredictionOrScanning(subrule.Begin, new ParseRecord(sequence, subrule.State, subrule.Begin), false);
              }
          }
          rp.Parse();
        }
        while (count < rp.Records[maxPos].Count);
      }
      while (maxPos < rp.MaxPos);

      return result;
    }

    private static HashSet<ParsedSequence> GetSequences(RecoveryParser rp, int maxPos)
    {
      return new SCG.HashSet<ParsedSequence>(rp.Records[maxPos].Select(r => r.Sequence));
    }

    const int s_loopState = 0;

    private void ContinueDeleteTokens(RecoveryParser rp, ParsedSequence sequence, int pos, int nextPos, int tokensToDelete)
    {
      DeleteToken(rp, sequence, pos, nextPos, false);

      var parseResult = rp.ParseResult;
      var grammar = parseResult.RuleParser.Grammar;
      var res2 = grammar.ParseAllVoidGrammarTokens(nextPos, parseResult);
      RemoveEmpty(res2, nextPos);

      if (res2.Count == 0)
        DeleteTokens(rp, nextPos, sequence, tokensToDelete - 1);
      foreach (var nextPos2 in res2)
      {
        //_deletedToken[new ParsedSequenceAndSubrule(sequence, new ParsedSubrule(pos, nextPos2, s_loopState))] = false;
        rp.SubruleParsed(nextPos, nextPos2, new ParseRecord(sequence, s_loopState, pos));
        DeleteTokens(rp, nextPos2, sequence, tokensToDelete - 1);
      }
    }

    #region |

    const int Root   = 0x01;
    const int Callee = 0x02;
    const int Caller = 0x04;
    const int Token  = 0x08;


    private void RecoverAllWays(RecoveryParser rp)
    {
      // ReSharper disable once RedundantAssignment
      int maxPos = rp.MaxPos;
      var failPositions = new HashSet<int>();
      var deleted = new List<Tuple<int, ParsedSequence>>();

      do
      {
        var tmpDeleted = FindMaxFailPos(rp);
        if (rp.MaxPos != rp.ParseResult.Text.Length)
          UpdateParseErrorCount();

        if (!CheckUnclosedToken(rp))
          deleted.AddRange(tmpDeleted);
        else
        {
        }

        maxPos = rp.MaxPos;
        failPositions.Add(maxPos);

        var tokens = GetCurrentTokens(rp, rp.MaxPos);

        if (tokens.Count > 0)
        {
          //ToDot(tokens.First().Token.Callers, "TokenRoots");
          //var root = rp.Sequences.First();

          var sequencesInProgress = new Dictionary<ParsingSequence, HashSet<ParsedSequence>>();
          var roots = CalcRoots(rp, maxPos, sequencesInProgress);

          //Mark(roots, Root);

          //ToDot(roots, "roots");

          var callers = CalcCallers(rp, tokens);

          //Mark(callers, Caller);

          //ToDot(callers, "callers");

          var callees = CalcCallees(roots);

          Mark(callees, Callee);

          //ToDot(callees, "callees");

          //ToDot(Enumerable.Concat(callees, callers), "all");

          var callPathSequences = new Dictionary<ParsingSequence, List<int>>();
          var callPath = CalcCallPath(callees, callers, callPathSequences);

          //ToDot(callPath, "callPath");

          UpdateParserState(callPathSequences, sequencesInProgress, maxPos, callPath);

          rp.Parse();
        }
        else if (rp.MaxPos == rp.ParseResult.Text.Length)
        {
          SkipAllStates(rp, maxPos, new SCG.Queue<ParseRecord>(rp.Records[maxPos]));
        }
        else
        {
          var oldMaxPos = rp.MaxPos;
          rp.Parse();
          Debug.Assert(oldMaxPos != rp.MaxPos);
        }
      }
      while (rp.MaxPos > maxPos);

      foreach (var del in deleted)
        DeleteTokens(rp, del.Item1, del.Item2, NumberOfTokensForSpeculativeDeleting);
      rp.Parse();
    }

    private static void Mark(HashSet<ParsingCallerInfo> callers, int mask)
    {
      foreach (var item in callers)
        item.Mask |= mask;
    }

    public static void Mark(Dictionary<ParsingCallerInfo, bool> roots, int mask)
    {
      foreach (var item in roots)
        item.Key.Mask |= mask;
    }

    private static HashSet<ParsingCallerInfo> CalcCallPath(HashSet<ParsingCallerInfo> callees, HashSet<ParsingCallerInfo> callers, Dictionary<ParsingSequence, List<int>> callPathSequences)
    {
      var callPath = new HashSet<ParsingCallerInfo>();

      foreach (var callee in callees)
      {
        if (callers.Contains(callee))
        {
          callPath.Add(callee);
          List<int> states;
          if (!callPathSequences.TryGetValue(callee.Sequence, out states))
          {
            states = new List<int>();
            callPathSequences.Add(callee.Sequence, states);
          }
          states.Add(callee.State);
        }
      }
      return callPath;
    }

    private HashSet<ParsingCallerInfo> CalcCallees(Dictionary<ParsingCallerInfo, bool> roots)
    {
      var callees = new HashSet<ParsingCallerInfo>();
      foreach (var callee in roots.Keys)
        FindAllCallees(callees, callee);
      return callees;
    }

    private HashSet<ParsingCallerInfo> CalcCallers(RecoveryParser _rp, List<TokenParserApplication> tokens)
    {
      foreach (var token in tokens)
        foreach (var caller in token.Token.Callers)
          _callerInfoMap[caller] = caller;


      var callers = new HashSet<ParsingCallerInfo>();
      foreach (var token in tokens)
      {
        Mark(token.Token.Callers, Token);

        //var yyy = rp.ParseResult.Text.Substring(token.Start, token.Length);
        //ToDot(token.Token.Callers);
        foreach (var callerInfo in token.Token.Callers)
          FindAllCallers(callers, callerInfo);
      }

      return callers;
    }

    private Dictionary<ParsingCallerInfo, bool> CalcRoots(RecoveryParser rp, int maxPos, Dictionary<ParsingSequence, HashSet<ParsedSequence>> sequencesInProgress)
    {
      var roots = new Dictionary<ParsingCallerInfo, bool>();
      var records = new SCG.Queue<ParseRecord>(rp.Records[maxPos]);
      foreach (var record in records)
        if (!record.IsComplete)
        {
          HashSet<ParsedSequence> sequences;
          if (!sequencesInProgress.TryGetValue(record.Sequence.ParsingSequence, out sequences))
          {
            sequences = new HashSet<ParsedSequence>();
            sequencesInProgress.Add(record.Sequence.ParsingSequence, sequences);
          }
          sequences.Add(record.Sequence);
          if (!(record.Sequence.IsToken && record.ParsingState.IsStart))
            AddRoot(roots, record.Sequence, record.State, maxPos);
          else
          {
          }
        }
      return roots;
    }

    private void UpdateParserState(
      Dictionary<ParsingSequence, List<int>> callPathSequences,
      Dictionary<ParsingSequence, HashSet<ParsedSequence>> sequencesInProgress,
      int maxPos,
      HashSet<ParsingCallerInfo> callPath)
    {
      foreach (var kv in callPathSequences)
      {
        var sequence = kv.Key;
        var states = kv.Value;
        HashSet<ParsedSequence> sequences;
        if (!sequencesInProgress.TryGetValue(sequence, out sequences))
        {
          sequences = new HashSet<ParsedSequence>();
          sequencesInProgress.Add(sequence, sequences);
        }

        foreach (var state in states)
        {
          if (sequence.States[state].IsStart)
          {
            sequences.Add(_recoveryParser.StartParseSequence(maxPos, sequence));
            break;
          }
        }

        if (sequences.Count == 0)
        {
          //сюда попадать не должны
        }

        foreach (var parsedSequence in sequences)
          foreach (var state in states)
            _recoveryParser.SubruleParsed(maxPos, maxPos, new ParseRecord(parsedSequence, state, maxPos));
      }

      foreach (var kv in callPathSequences)
      {
        var sequence = kv.Key;
        var states = kv.Value;
        foreach (var state in states)
          if (sequence.States[state].IsStart)
          {
            foreach (var caller in sequence.Callers)
              if (callPath.Contains(caller))
                foreach (var parsedSequence in sequencesInProgress[caller.Sequence])
                  _recoveryParser.StartParseSequence(new ParseRecord(parsedSequence, caller.State, maxPos), maxPos, sequence);
            break;
          }
      }
    }

    private void AddRoot(Dictionary<ParsingCallerInfo, bool> roots, ParsedSequence parsedSequence, int state, int textPos)
    {
      var parsingSequence = parsedSequence.ParsingSequence;

      if (roots.ContainsKey(CreateParsingCallerInfo(parsedSequence.ParsingSequence, state)))
        return;

      var toProcess = new SCG.Stack<int>();
      toProcess.Push(state);
      while (toProcess.Count > 0)
      {
        var curState = toProcess.Pop();
        var key = CreateParsingCallerInfo(parsedSequence.ParsingSequence, curState);
        if (!roots.ContainsKey(key))
        {
          foreach (var nextState in parsingSequence.States[curState].Next)
            if (nextState >= 0)
              toProcess.Push(nextState);
          roots.Add(key, false);
          _recoveryParser.SubruleParsed(textPos, textPos, new ParseRecord(parsedSequence, curState, textPos));
        }
      }

      foreach (var caller in parsedSequence.Callers)
        AddRoot(roots, caller.Sequence, caller.State, textPos);
    }

    private void FindAllCallers(HashSet<ParsingCallerInfo> callers, ParsingCallerInfo caller)
    {
      if (!callers.Add(caller))
        return;

      var statesToAdd = new SCG.Stack<int>();
      statesToAdd.Push(caller.State);
      while (statesToAdd.Count > 0)
      {
        var state = statesToAdd.Pop();
        foreach (var prevState in caller.Sequence.States[state].Prev)
          if (callers.Add(CreateParsingCallerInfo(caller.Sequence, prevState)))
            statesToAdd.Push(prevState);
      }

      foreach (var seqCaller in caller.Sequence.Callers)
        FindAllCallers(callers, seqCaller);
    }

    private void FindAllCallees(HashSet<ParsingCallerInfo> callees, ParsingCallerInfo callee)
    {
      if (callee.ToString().Contains("VariableDeclarators"))
      {
      }
      if (!callees.Add(callee))
        return;

      var statesToAdd = new SCG.Stack<int>();
      statesToAdd.Push(callee.State);
      while (statesToAdd.Count > 0)
      {
        var state = statesToAdd.Pop();
        foreach (var calleeSequence in callee.Sequence.States[state].CalleeSequences)
          foreach (var startState in calleeSequence.StartStates)
            if (startState != -1)
              FindAllCallees(callees, CreateParsingCallerInfo(calleeSequence, startState));
        foreach (var nextState in callee.Sequence.States[state].Next)
          if (nextState != -1 && callees.Add(CreateParsingCallerInfo(callee.Sequence, nextState)))
            statesToAdd.Push(nextState);
      }
    }

    Dictionary<ParsingCallerInfo, ParsingCallerInfo> _callerInfoMap;

    ParsingCallerInfo CreateParsingCallerInfo(ParsingSequence sequence, int state)
    {
      var key = new ParsingCallerInfo(sequence, state);
      ParsingCallerInfo result;
      if (_callerInfoMap.TryGetValue(key, out result))
        return result;

      _callerInfoMap[key] = key;
      return key;
    }

    #endregion

    private bool CheckUnclosedToken(RecoveryParser rp)
    {
      var maxPos  = rp.MaxPos;
      var grammar = rp.ParseResult.RuleParser.Grammar;
      var records = rp.Records[maxPos].ToArray();
      var result  = new SCG.Dictionary<ParseRecord, bool>();
      var unclosedTokenFound = false;

      foreach (var record in records)
      {
        if (record.IsComplete)
          continue;

        if (record.Sequence.StartPos >= maxPos)
          continue;

        if (record.Sequence.ParsingSequence.IsNullable)
          continue;

        var res = IsInsideToken(result, grammar, record);

        if (!res)
          continue;

        unclosedTokenFound = true;
        rp.SubruleParsed(maxPos, maxPos, record);
      }

      return unclosedTokenFound;
    }

    #region Deletion

    /// <summary>
    /// В позиции облома может находиться "грязь", т.е. набор символов которые не удается разобрать ни одним правилом токена
    /// доступным в CompositeGrammar в данном месте. Ни одно правило не сможет спарсить этот код, так что просто ищем 
    /// следующий корректный токе и пропускаем все что идет до него (грязь).
    /// </summary>
    /// <returns>true - если грязь была удалена</returns>
    private bool TryDeleteGarbage(RecoveryParser rp, int maxPos, ParsedSequence sequence)
    {
      var text = rp.ParseResult.Text;
      if (maxPos >= text.Length)
        return false;
      var parseResult = rp.ParseResult;
      var grammar = parseResult.RuleParser.Grammar;
      var res = grammar.ParseAllGrammarTokens(maxPos, parseResult);
      RemoveEmpty(res, maxPos);

      if (res.Count == 0)
      {
        var i = maxPos + 1;
        for (; i < text.Length; i++) // крутимся пока не будет распознан токен или достигнут конец строки
        {
          var res2 = grammar.ParseAllGrammarTokens(i, parseResult);
          RemoveEmpty(res2, i);
          if (res2.Count > 0)
            break;
        }

        DeleteToken(rp, sequence, maxPos, i, true);
        return true;
      }

      return false;
    }

    private void DeleteToken(RecoveryParser rp, ParsedSequence sequence, int beingPos, int endPos, bool isToken)
    {
      var listSequence = rp.StartParseSequence(new ParseRecord(sequence, 0, sequence.StartPos), sequence.StartPos, ((ParsingState.List)sequence.ParsingSequence.States[0]).Sequence);
      _deletedToken[new ParsedSequenceAndSubrule(listSequence, new ParsedSubrule(beingPos, endPos, s_loopState))] = isToken;
      rp.SubruleParsed(beingPos, endPos, new ParseRecord(listSequence, 0, beingPos));
    }

    private void DeleteTokens(RecoveryParser rp, int pos, ParsedSequence sequence, int tokensToDelete)
    {
      if (tokensToDelete <= 0)
        return;

      var text = rp.ParseResult.Text;
      var parseResult = rp.ParseResult;
      var grammar = parseResult.RuleParser.Grammar;
      var res = grammar.ParseAllNonVoidGrammarTokens(pos, parseResult);
      RemoveEmpty(res, pos);

      if (res.Count == 0)
        return;

      foreach (var nextPos in res)
        if (CanDelete(text, pos, nextPos))
          ContinueDeleteTokens(rp, sequence, pos, nextPos, tokensToDelete);
    }

    private List<TokenParserApplication> GetCurrentTokens(RecoveryParser rp, int pos)
    {
      var parseResult = rp.ParseResult;
      var grammar = parseResult.RuleParser.Grammar;
      var res = grammar.ParseNonVoidTokens(pos, parseResult);
      return res;
    }

    private bool CanDelete(string text, int pos, int nextPos)
    {
      // TODO: Надо неализовать эту функцию на базе метаинформации из грамматик.
      switch (text.Substring(pos, nextPos - pos))
      {
        case ",":
        case ";": return false;
        default: return true;
      }
    }

    #endregion

    private void SkipAllStates(RecoveryParser rp, int maxPos, SCG.Queue<ParseRecord> records)
    {
      _nestedLevel = 0;
      var records2 = new HashSet<ParseRecord>();
      do
      {
        while (records.Count > 0)
          SkipAllStates(records.Dequeue(), maxPos, records2);

        foreach (var tuple in rp.RecordsToProcess)
          records.Enqueue(tuple.Field1);

        rp.Parse();
      } while (records.Count > 0);
    }

    private static int _nestedLevel;

    private void SkipAllStates(ParseRecord record, int pos, HashSet<ParseRecord> visited)
    {
      if (visited.Contains(record))
        return;

      _nestedLevel++;

      if (_nestedLevel == 1000)
      {
      }

      visited.Add(record);

      foreach (var caller in record.Sequence.Callers)
        SkipAllStates(caller, pos, visited);

      if (!record.IsComplete)
      {
        _recoveryParser.SubruleParsed(pos, pos, record);

        foreach (var nextState in record.ParsingState.Next)
        {
          if (nextState >= 0)
          {
            var next = record.Next(nextState);
            SkipAllStates(next, pos, visited);
          }
        }
      }

      _nestedLevel--;
    }

    private static bool IsInsideToken(SCG.Dictionary<ParseRecord, bool> memoization, CompositeGrammar compositeGrammar, ParseRecord record)
    {
      bool res;
      if (memoization.TryGetValue(record, out res))
        return res;

      if (record.Sequence.ParsingSequence.SequenceInfo is SequenceInfo.Ast)
      {
        var parser = record.Sequence.ParsingSequence.SequenceInfo.Parser;
        res = compositeGrammar.Tokens.ContainsKey(parser) || compositeGrammar.VoidTokens.ContainsKey(parser);
        memoization[record] = res;
        if(res)
          return res;
      }

      foreach (var caller in record.Sequence.Callers)
      {
        res = IsInsideToken(memoization, compositeGrammar, caller);
        if (res)
        {
          memoization[record] = true;
          return true;
        }
      }

      memoization[record] = false;
      return false;
    }

    private static void RemoveEmpty(HashSet<int> res, int maxPos)
    {
      res.RemoveWhere(x => x <= maxPos);
    }

    #region Dot

    public void ToDot(Dictionary<ParsingCallerInfo, bool> roots, string namePrefix)
    {
      ToDot(roots.Select(r => r.Key), namePrefix);
    }

    public void ToDot(ParsingCallerInfo callerInfo, string namePrefix)
    {
      ToDot(Enumerable.Repeat(callerInfo, 1), namePrefix);
    }

    private void ToDot(IEnumerable<ParsingCallerInfo> callerInfos, string namePrefix)
    {
      var sb = new StringBuilder();
      sb.Append(@"
        digraph RecoveryParser
        {
          compound=true;
          label=");
      sb.Append('\"');
      sb.Append(namePrefix);
      sb.Append('\"');
      sb.AppendLine();

      var visited = new HashSet<ParsingCallerInfo>();
      var created  = new HashSet<ParsingCallerInfo>();
      foreach (var callerInfo in callerInfos)
        ToDot(sb, visited, callerInfo, true, created);

      created.ExceptWith(visited);

      foreach (var callerInfo in created)
        sb.AppendLine(Name(callerInfo) + "[label=\"" + Label(callerInfo) + "\" shape=box color=purple];");

      sb.Append(@"}");

      var fileName = Path.Combine(Path.GetTempPath(), namePrefix + ".dot");
      File.WriteAllText(fileName, sb.ToString());
      X.ConvertToDot(fileName);
    }

    string Name(ParsingCallerInfo callerInfo)
    {
      return "Node_" + callerInfo.GetHashCode();
    }

    string Label(ParsingCallerInfo callerInfo)
    {
      string prefix = "";

      if (HasMask(callerInfo, Root))
        prefix += "~";

      if (HasMask(callerInfo, Token))
        prefix += "%";

      if (HasMask(callerInfo, Callee))
        prefix += ">";

      if (HasMask(callerInfo, Caller))
        prefix += "<";

      var str = prefix + callerInfo.State + " " + callerInfo.ToString();

      if (!string.IsNullOrWhiteSpace(callerInfo.Sequence.RuleName))
        str = callerInfo.Sequence.RuleName + "\r\n" + str;

      return X.DotEscape(str);
    }

    public string GetStyle(ParsingCallerInfo callerInfo, int mask, string style)
    {
      return HasMask(callerInfo, mask) ? style : "";
    }

    static bool HasMask(ParsingCallerInfo callerInfo, int  mask)
    {
      return (callerInfo.Mask & mask) == mask;
    }

    private void ToDot(StringBuilder sb, HashSet<ParsingCallerInfo> visited, ParsingCallerInfo callerInfo, bool isStart, HashSet<ParsingCallerInfo> created)
    {
      if (string.Equals(callerInfo.Sequence.RuleName, "s", StringComparison.InvariantCultureIgnoreCase))
        return;

      if (!visited.Add(callerInfo))
        return;

      const string StartStyle  = " peripheries=2";
      
      var style = isStart ? StartStyle : "";
      var state = callerInfo.Sequence.States[callerInfo.State];

      if (HasMask(callerInfo, Token))
        style += " color=red";
      else if (HasMask(callerInfo, Root))
        style += " color=blue";
      //else if (HasMask(callerInfo, Caller))
      //  style += " color=green";

      //if (HasMask(callerInfo, Root))
      //  style += " color=purple";

      var id = Name(callerInfo);
      sb.AppendLine(id + "[label=\"" + Label(callerInfo) + "\" shape=box" + style + "];");

      foreach (var prev in state.Prev)
      {
        var callerInfoPrev = new ParsingCallerInfo(callerInfo.Sequence, prev);
        created.Add(callerInfoPrev);
        var idPrev = Name(callerInfoPrev);
        sb.AppendLine(idPrev + " -> " + id + ";");
      }

      if (callerInfo.Sequence.States[callerInfo.State].IsStart)
      {
        foreach (var caller in callerInfo.Sequence.Callers)
          sb.AppendLine(Name(caller) + " -> " + id + "[color=blue]" + ";");

        foreach (var caller in callerInfo.Sequence.Callers)
          ToDot(sb, visited, caller, false, created);
      }
    }

    #endregion

    protected virtual void UpdateEarleyParseTime() { }
    protected virtual void UpdateRecoverAllWaysTime() { }
    protected virtual void UpdateFindBestPathTime() { }
    protected virtual void UpdateFlattenSequenceTime() { }
    protected virtual void UpdateParseErrorCount() { }
  }

  #region Utility methods

  public static class ParsePathsVisializer
  {
    #region HtmlTemplate
    private const string HtmlTemplate = @"
<html>
<head>
    <title>Pretty Print</title>
    <meta http-equiv='Content-Type' content='text/html;charset=utf-8'/>
    <style type='text/css'>
pre
{
  color: black;
  font-weight: normal;
  font-size: 12pt;
  font-family: Consolas, Courier New, Monospace;
}

.default
{
  color: black;
  background: white;
}

.garbage
{
  color: rgb(243, 212, 166);
  background: rgb(170, 134, 80);
}

.deleted
{
  background: lightpink;
}

.parsed
{
  color: Green;
  background: LightGreen;
}

.prefix
{
  color: Indigo;
  background: Plum;
}

.postfix
{
  color: blue;
  background: lightgray;
}

.skipedState
{
  color: darkgray;
  background: lightgray;
  -webkit-print-color-adjust:exact;
}
.currentRulePrefix
{
  color: darkgoldenrod;
  background: lightgoldenrodyellow;
}
</style>
</head>
<body>
<pre>
<content/>
</pre>
</body>
</html>
";
    #endregion

    static readonly XAttribute _garbageClass = new XAttribute("class", "garbage");
    static readonly XAttribute _deletedClass = new XAttribute("class", "deleted");
    static readonly XAttribute _skipedStateClass = new XAttribute("class", "skipedState");
    static readonly XAttribute _default = new XAttribute("class", "default");

    public static void PrintPaths(ParseResult parseResult, Dictionary<ParsedSequenceAndSubrule, bool> deletedToken, FlattenSequences paths)
    {
      var results = new List<XNode> { new XText(RecoveryDebug.CurrentTestName + "\r\n" + parseResult.DebugText + "\r\n\r\n") };

      foreach (var path in paths)
        PrintPath(results, parseResult.Text, deletedToken, path);

      var template = XElement.Parse(HtmlTemplate);
      var content = template.Descendants("content").First();
      Debug.Assert(content.Parent != null);
      content.Parent.ReplaceAll(results);
      var filePath = Path.ChangeExtension(Path.GetTempFileName(), ".html");
      template.Save(filePath);
      Process.Start(filePath);
    }

    public static void PrintPath(List<XNode> results, string text, Dictionary<ParsedSequenceAndSubrule, bool> deletedToken, ParsedSequenceAndSubrules path)
    {
      var isPrevInsertion = false;

      foreach (var node in path.Reverse())
      {
        var tokenChanges = node.TokenChanges;
        var seq = node.Seq;
        var subrule = node.Subrule;
        
        bool isGarbage;
        if (deletedToken.TryGetValue(new ParsedSequenceAndSubrule(seq, subrule), out isGarbage))// TODO: Возможно здесь надо проверять значение tokenChanges.Deleted
        {
          isPrevInsertion = false;
          var title = new XAttribute("title", "Deleted token;  Subrule: " + subrule + ";  Sequence: " + seq + ";");
          results.Add(new XElement("span", isGarbage ? _garbageClass : _deletedClass,
            title, text.Substring(subrule.Begin, subrule.End - subrule.Begin)));
        }
        else if (tokenChanges.HasChanges)
        {
          var desc = seq.ParsingSequence.States[subrule.State].Description;
          if (!subrule.IsEmpty)
          { }
          var title = new XAttribute("title", "Inserted tokens: " + tokenChanges + ";  Subrule: " + subrule + ";  Sequence: " + seq + ";");
          results.Add(new XElement("span", _skipedStateClass, title, isPrevInsertion ? " " + desc : desc));
          isPrevInsertion = true;
        }
        else
        {
          var desc = seq.ParsingSequence.States[subrule.State].Description;
          var title = new XAttribute("title", "Description: " + desc + ";  Subrule: " + subrule + ";  Sequence: " + seq + ";");
          results.Add(new XElement("span", title, text.Substring(subrule.Begin, subrule.End - subrule.Begin)));

          isPrevInsertion = false;
        }
      }

      results.Add(new XText("\r\n"));
    }
  }

  #endregion
}
