﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace adb
{
    public class ProfileOption
    {
        public bool enabled_ = false;
    }

    public class OptimizeOption
    {
        // rewrite controls
        public bool enable_subquery_to_markjoin_ = true;
        public bool enable_hashjoin_ = true;
        public bool enable_nljoin_ = true;
        
        // optimizer controls
        public bool use_memo_ = false;
    }

    public abstract class PlanNode<T> where T : PlanNode<T>
    {
        public List<T> children_ = new List<T>();
        public bool IsLeaf() => children_.Count == 0;

        // shortcut for conventional names
        public T child_() { Debug.Assert(children_.Count == 1); return children_[0]; }
        public T l_() { Debug.Assert(children_.Count == 2); return children_[0]; }
        public T r_() { Debug.Assert(children_.Count == 2); return children_[1]; }

        // print utilities
        public virtual string PrintOutput(int depth) => null;
        public virtual string PrintInlineDetails(int depth) => null;
        public virtual string PrintMoreDetails(int depth) => null;
        protected string PrintFilter(Expr filter, int depth)
        {
            string r = null;
            if (filter != null)
            {
                r = "Filter: " + filter.PrintString(depth);
                // append the subquery plan align with filter
                r += ExprHelper.PrintExprWithSubqueryExpanded(filter, depth);
            }
            return r;
        }

        public string PrintString(int depth)
        {
            string r = null;
            if (!(this is PhysicProfiling))
            {
                r = Utils.Tabs(depth);
                if (depth != 0)
                    r += "-> ";
                r += $"{this.GetType().Name} {PrintInlineDetails(depth)}";
                if (this is PhysicNode && (this as PhysicNode).profile_ != null)
                    r += $"  (rows = {(this as PhysicNode).profile_.nrows_})";
                r += "\n";
                var details = PrintMoreDetails(depth);

                // output of current node
                var output = PrintOutput(depth);
                if (output != null)
                    r += Utils.Tabs(depth + 2) + output + "\n";
                if (details != null)
                {
                    // remove the last \n in case the details is a subquery
                    var trailing = "\n";
                    if (details[details.Length - 1] == '\n')
                        trailing = "";
                    r += Utils.Tabs(depth + 2) + details + trailing;
                }

                depth += 2;
            }

            children_.ForEach(x => r += x.PrintString(depth));
            return r;
        }

        // traversal pattern EXISTS
        //  if any visit returns a true, stop recursion. So if you want to
        //  visit all nodes, your callback shall always return false
        //
        public bool VisitEachNodeExists(Func<PlanNode<T>, bool> callback)
        {
            if (callback(this))
                return true;
            else
            {
                foreach (var c in children_)
                    if (c.VisitEachNodeExists(callback))
                        return true;
                return false;
            }
        }

        // traversal pattern FOR EACH
        public void TraversEachNode(Action<PlanNode<T>> callback)
        {
            callback(this);
            foreach (var c in children_)
                c.TraversEachNode(callback);
        }

        // lookup all T1 types in the tree and return the parent-target relationship
        public int FindNodeTyped<T1>(List<T> parents, List<int> childIndex, List<T1> targets) where T1 : PlanNode<T>
        {
            if (this is T1 yf)
            {
                parents.Add(null);
                childIndex.Add(-1);
                targets.Add(yf);
            }

            TraversEachNode(x =>
            {
                for (int i = 0; i < x.children_.Count; i++)
                {
                    var y = x.children_[i];
                    if (y is T1 yf)
                    {
                        parents.Add(x as T);
                        childIndex.Add(i);
                        targets.Add(yf);
                    }
                }
            });

            Debug.Assert(parents.Count == targets.Count);
            return parents.Count;
        }

        public int CountNodeTyped<T1>() where T1 : PlanNode<T> {
            var parents = new List<T>();
            var indexes = new List<int>();
            var targets = new List<T1>();
            return FindNodeTyped<T1>(parents, indexes, targets);
        }
        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ Utils.ListHashCode(children_);
        }
        public override bool Equals(object obj)
        {
            if (obj is PlanNode<T> lo)
            {
                if (lo.GetType() != GetType())
                    return false;
                for (int i = 0; i < children_.Count; i++)
                {
                    if (!lo.children_[i].Equals(children_[i]))
                        return false;
                }
                return true;
            }
            return false;
        }
    }

    public abstract class LogicNode : PlanNode<LogicNode>
    {
        public Expr filter_ = null;
        public List<Expr> output_ = new List<Expr>();

        public override string PrintMoreDetails(int depth) => PrintFilter(filter_, depth);

        public override string PrintOutput(int depth)
        {
            if (output_.Count != 0)
            {
                string r = "Output: " + string.Join(",", output_);
                output_.ForEach(x => r += ExprHelper.PrintExprWithSubqueryExpanded(x, depth));
                return r;
            }
            return null;
        }

        // This is an honest translation from logic to physical plan
        public PhysicNode DirectToPhysical(ProfileOption profiling)
        {
            PhysicNode root = null;
            TraversEachNode(n =>
            {
                PhysicNode phy;
                switch (n)
                {
                    case LogicScanTable ln:
                        phy = new PhysicScanTable(ln);
                        if (ln.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(ln.filter_);
                        break;
                    case LogicJoin lc:
                        var l = n.l_();
                        var r = n.r_();
                        switch (lc)
                        {
                            case LogicSingleMarkJoin lsmj:
                                phy = new PhysicSingleMarkJoin(lsmj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            case LogicMarkJoin lmj:
                                phy = new PhysicMarkJoin(lmj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            case LogicSingleJoin lsj:
                                phy = new PhysicSingleJoin(lsj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            default:
                                bool lhasouter = TableRef.HasOuterRefs(l.InclusiveTableRefs());
                                bool rhasouter = TableRef.HasOuterRefs(r.InclusiveTableRefs());

                                // if left side has outerrefs, we needs NLJ to drive the parameter
                                // pass to right side, so we can't use HJ in this case.
                                if (lc.FilterHashable() && !lhasouter)
                                    phy = new PhysicHashJoin(lc,
                                        l.DirectToPhysical(profiling),
                                        r.DirectToPhysical(profiling));
                                else
                                {
                                    // it is ok right side reference left side's variable but not 
                                    // the other direction. In this case, we shall flip the side.
                                    //
                                    phy = new PhysicNLJoin(lc,
                                        l.DirectToPhysical(profiling),
                                        r.DirectToPhysical(profiling));
                                }
                                break;
                        }
                        break;
                    case LogicResult lr:
                        phy = new PhysicResult(lr);
                        break;
                    case LogicFromQuery ls:
                        phy = new PhysicFromQuery(ls, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicFilter lf:
                        phy = new PhysicFilter(lf, n.child_().DirectToPhysical(profiling));
                        if (lf.filter_ != null)
                            ExprHelper.SubqueryDirectToPhysic(lf.filter_);
                        break;
                    case LogicInsert li:
                        phy = new PhysicInsert(li, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicScanFile le:
                        phy = new PhysicScanFile(le);
                        break;
                    case LogicAgg la:
                        phy = new PhysicHashAgg(la, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicOrder lo:
                        phy = new PhysicOrder(lo, n.child_().DirectToPhysical(profiling));
                        break;
                    default:
                        throw new NotImplementedException();
                }

                if (profiling.enabled_)
                    phy = new PhysicProfiling(phy);

                if (root is null)
                    root = phy;
            });

            return root;
        }

        public virtual int MemoLogicSign() => GetHashCode();

        public List<TableRef> InclusiveTableRefs()
        {
            List<TableRef> refs = new List<TableRef>();
            TraversEachNode(x =>
            {
                if (x is LogicScanTable gx)
                    refs.Add(gx.tabref_);
                else if (x is LogicFromQuery fx)
                {
                    refs.Add(fx.queryRef_);
                    refs.AddRange(fx.queryRef_.query_.bindContext_.AllTableRefs());
                }
            });
            return refs;
        }

        // resolve mapping from children output
        // 1. you shall first compute the reqOutput by accouting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        public virtual List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true) => null;

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source)
        {
            var clone = toclone.Clone();

            // first try to match the whole expression - don't do this for ColExpr
            // because it has no practial benefits.
            // 
            if (!(clone is ColExpr))
            {
                int ordinal = source.FindIndex(clone.Equals);
                if (ordinal != -1)
                    return new ExprRef(clone, ordinal);
            }

            // we have to use each ColExpr and fix its ordinal
            clone.VisitEachExpr(y =>
            {
                if (y is ColExpr target)
                {
                    Predicate<Expr> nameTest;
                    nameTest = z => target.Equals(z) || y.alias_.Equals(z.alias_);

                    // using source's matching index for ordinal
                    // fix colexpr's ordinal - leave the outerref as it is already handled in ColExpr.Bind()
                    if (!target.isOuterRef_)
                    {
                        target.ordinal_ = source.FindIndex(nameTest);

                        // we may hit more than one target, say t2.col1 matching {t1.col1, t2.col1}
                        // in this case, we shall redo the mapping with table name
                        //
                        Debug.Assert (source.FindAll(nameTest).Count >= 1);
                        if (source.FindAll(nameTest).Count > 1) {
                            nameTest = z => z is ColExpr 
                                    && target.alias_.Equals(z.alias_) 
                                    && target.tabRef_.Equals((z as ColExpr).tabRef_);
                            target.ordinal_ = source.FindIndex(nameTest);
                            Debug.Assert(source.FindAll(nameTest).Count == 1);
                        }
                    }
                    Debug.Assert(target.ordinal_ != -1);
                }
            });

            return clone;
        }

        // fix each expression by using source's ordinal and make a copy
        internal List<Expr> CloneFixColumnOrdinal(List<Expr> toclone, List<Expr> source)
        {
            var clone = new List<Expr>();
            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source)));
            Debug.Assert(clone.Count == toclone.Count);
            return clone;
        }

    }

    // LogicMemoRef wrap a CMemoGroup as a LogicNode (so CMemoGroup can be used in plan tree)
    //
    public class LogicMemoRef : LogicNode {
        public CMemoGroup group_;

        public LogicNode Deref() => child_();
        public T Deref<T>() where T: LogicNode => (T)Deref();

        public LogicMemoRef(CMemoGroup group)
        {
            Debug.Assert(group != null);
            var child = group.exprList_[0].logic_;

            children_.Add(child);
            group_ = group;

            Debug.Assert(filter_ is null);
            Debug.Assert(!(Deref() is LogicMemoRef));
            Debug.Assert(group.memo_.LookupCGroup(Deref()) == group);
        }
        public override string ToString() => group_.ToString();

        public override int MemoLogicSign() => Deref().MemoLogicSign();
        public override int GetHashCode() => MemoLogicSign();
        public override bool Equals(object obj) 
        {
            if (obj is LogicMemoRef lo)
                return lo.MemoLogicSign() == MemoLogicSign();
            return false;
        }

        public override string PrintMoreDetails(int depth)
        {
            // we want to see what's underneath
            return $"{{{Deref().PrintString(depth + 1)}}}";
        }
    }

    enum JoinType {
        // ANSI SQL specified join types can show in SQL statement
        InnerJoin,
        LeftJoin,
        RightJoin,
        FullJoin,
        CrossJoin
            ,
        // these are used by subquery expansion or optimizations (say PK/FK join)
        SemiJoin,
        AntiSemiJoin,
    };

    public class LogicJoin : LogicNode
    {
        internal JoinType type_ = JoinType.InnerJoin;
        public override string ToString() => $"({l_()} {type_} {r_()})";
        public LogicJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }
        public LogicJoin(LogicNode l, LogicNode r, Expr filter): this(l, r) 
        { 
            filter_ = filter; 
        }

        public override int MemoLogicSign() {
            var filterhash = 0;
            if (filter_ != null) {
                // consider the case:
                //   A X (B X C on f3) on f1 AND f2
                // is equal to commutative transformation
                //   (A X B on f1) X C on f3 AND f2
                // The filter signature generation has to be able to accomomdate this difference.
                //
                var andlist = FilterHelper.FilterToAndList(filter_);
                filterhash = Utils.ListHashCode(andlist);
                //filterhash = filter_.GetHashCode();
            }
            return l_().MemoLogicSign() ^ r_().MemoLogicSign() ^ filterhash;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ (filter_?.GetHashCode() ?? 0);
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicJoin lo) {
                return base.Equals(lo) && (filter_?.Equals(lo.filter_) ?? true);
            }
            return false;
        }

        // forms to consider:
        //   a.i = b.j
        //   a.i = b.j and b.l = a.k
        //   (a.i, a.k) = (b.j, b.l)
        //   a.i + b.i = c.i-2*d.i if left side contained a,b and right side c,d
        // but not:
        //   a.i = c.i-2*d.i-b.i if left side contained a,b and right side c,d (we can add later)
        //
        bool OneFilterHashable(Expr filter)
        {
            if (filter is BinExpr bf && bf.op_.Equals("="))
            {
                var ltabrefs = bf.l_().tableRefs_;
                var rtabrefs = bf.r_().tableRefs_;
                // TODO: a.i+b.i=0 => a.i=-b.i
                return ltabrefs.Count > 0 && rtabrefs.Count > 0;
            }
            return false;
        }
        public bool FilterHashable()
        {
            Expr filter = filter_;
            bool general = false;   // FIXME

            if (general)
            {
                var andlist = FilterHelper.FilterToAndList(filter);
                foreach (var v in andlist)
                {
                    if (OneFilterHashable(v))
                        return false;
                }
                return true;
            }
            else
                return OneFilterHashable (filter);
        }

        public bool AddFilter(Expr filter)
        {
            filter_ = FilterHelper.AddAndFilter(filter_, filter);
            return true;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>(reqOutput);
            if (filter_ != null)
                reqFromChild.Add(filter_);

            // push to left and right: to which side depends on the TableRef it contains
            var ltables = l_().InclusiveTableRefs();
            var rtables = r_().InclusiveTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();
            foreach (var v in reqFromChild)
            {
                var tables = ExprHelper.AllTableRef(v);

                if (Utils.ListAContainsB(ltables, tables))
                    lreq.Add(v);
                else if (Utils.ListAContainsB(rtables, tables))
                    rreq.Add(v);
                else
                {
                    // the whole list can't push to the children (Eg. a.a1 + b.b1)
                    // decompose to singleton and push down
                    var colref = ExprHelper.RetrieveAllColExpr(v);
                    colref.ForEach(x =>
                    {
                        if (ltables.Contains(x.tabRef_))
                            lreq.Add(x);
                        else if (rtables.Contains(x.tabRef_))
                            rreq.Add(x);
                        else
                            throw new InvalidProgramException("contains invalid tableref");
                    });
                }
            }

            // get left and right child to resolve columns
            l_().ResolveColumnOrdinal(lreq.ToList());
            var lout = l_().output_;
            r_().ResolveColumnOrdinal(rreq.ToList());
            var rout = r_().output_;
            Debug.Assert(lout.Intersect(rout).Count() == 0);

            // assuming left output first followed with right output
            var childrenout = lout.ToList(); childrenout.AddRange(rout.ToList());
            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, childrenout);
            output_ = CloneFixColumnOrdinal(reqOutput, childrenout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicFilter : LogicNode
    {
        public override string ToString() => $"{children_[0]} filter: {filter_}";

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ filter_.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicFilter lo)
            {
                return base.Equals(lo) && filter_.Equals(lo.filter_);
            }
            return false;
        }

        public LogicFilter(LogicNode child, Expr filter)
        {
            children_.Add(child); filter_ = filter;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(ExprHelper.CloneList(reqOutput));
            reqFromChild.AddRange(ExprHelper.RetrieveAllColExpr(filter_));
            children_[0].ResolveColumnOrdinal(reqFromChild);
            var childout = children_[0].output_;

            filter_ = CloneFixColumnOrdinal(filter_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();

            return ordinals;
        }
    }

    public class LogicAgg : LogicNode
    {
        internal List<Expr> keys_;
        internal Expr having_;

        // runtime info: derived from output request
        internal List<Expr> aggrCore_ = new List<Expr>();
        public override string ToString() => $"Agg({child_()})";

        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (aggrCore_ != null)
                r += $"Agg Core: {string.Join(", ", aggrCore_)}\n";
            if (keys_ != null)
                r += Utils.Tabs(depth + 2) + $"Group by: {string.Join(", ", keys_)}\n";
            if (having_ != null)
                r += Utils.Tabs(depth + 2) + $"{PrintFilter(having_, depth)}";
            return r;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, List<Expr> aggrs, Expr having)
        {
            children_.Add(child); keys_ = groupby; having_ = having;
        }

        List<Expr> removeAggFuncFromOutput(List<Expr> reqOutput)
        {
            var reqList = ExprHelper.CloneList(reqOutput, new List<Type> { typeof(LiteralExpr) });
            var aggs = new List<Expr>();
            reqList.ForEach(x =>
                x.VisitEachExpr(y =>
                {
                    // 1+abs(min(a))+max(b)
                    if (y is AggFunc ay)
                        aggs.Add(x);
                }));

            // aggs remove functions
            aggs.ForEach(x =>
            {
                reqList.Remove(x);
                bool check = false;
                x.VisitEachExpr(y =>
                {
                    if (y is AggFunc ay)
                    {
                        check = true;
                        reqList.AddRange(ay.GetNonFuncExprList());
                    }
                });
                Debug.Assert(check);
            });

            reqList = reqList.Distinct().ToList();
            return reqList;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(removeAggFuncFromOutput(reqOutput));
            if (keys_ != null) reqFromChild.AddRange(ExprHelper.RetrieveAllColExpr(keys_));
            children_[0].ResolveColumnOrdinal(reqFromChild);
            var childout = children_[0].output_;

            if (keys_ != null) keys_ = CloneFixColumnOrdinal(keys_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();

            // Bound aggrs to output, so when we computed aggrs, we automatically get output
            // Here is an example:
            //  output_: <literal>, cos(a1*7)+sum(a1),  sum(a1) + sum(a2+a3)*2
            //                       |           \       /          |   
            //                       |            \     /           |   
            //  keys_:               a1            \   /            |
            //  aggrCore_:                        sum(a1),      sum(a2+a3)
            // =>
            //  output_: <literal>, cos(ref[0]*7)+ref[1],  ref[1]+ref[2]*2
            //
            var nkeys = keys_?.Count ?? 0;
            var newoutput = new List<Expr>();
            if (keys_ != null) output_ = Utils.SearchReplace(output_, keys_);
            output_.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    if (y is AggFunc ya)
                    {
                        // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                        if (!aggrCore_.Contains(ya))
                            aggrCore_.Add(ya);
                        x = x.SearchReplace(y, new ExprRef(y, nkeys + aggrCore_.IndexOf(y)));
                    }
                });

                newoutput.Add(x);
            });
            Debug.Assert(aggrCore_.Count == aggrCore_.Distinct().Count());

            // Say invvalid expression means contains colexpr, then the output shall contains
            // no expression consists invalid expression
            //
            Expr offending = null;
            newoutput.ForEach(x =>
            {
                if (x.VisitEachExprExists(y => y is ColExpr, new List<Type> { typeof(ExprRef) }))
                    offending = x;
            });
            if (offending != null)
                throw new SemanticAnalyzeException($"column {offending} must appear in group by clause");
            output_ = newoutput;

            return ordinals;
        }

    }

    public class LogicOrder : LogicNode
    {
        internal List<Expr> orders_ = new List<Expr>();
        internal List<bool> descends_ = new List<bool>();
        public override string PrintMoreDetails(int depth)
        {
            var r = $"Order by: {string.Join(", ", orders_)}\n";
            return r;
        }

        public LogicOrder(LogicNode child, List<Expr> orders, List<bool> descends)
        {
            children_.Add(child);
            orders_ = orders;
            descends_ = descends;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(ExprHelper.CloneList(reqOutput));
            reqFromChild.AddRange(orders_);
            children_[0].ResolveColumnOrdinal(reqFromChild);
            var childout = children_[0].output_;

            orders_ = CloneFixColumnOrdinal(orders_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicFromQuery : LogicNode
    {
        public QueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>({child_()})";
        public override string PrintInlineDetails(int depth) => $"<{queryRef_.alias_}>";
        public LogicFromQuery(QueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            var query = queryRef_.query_;
            query.logicPlan_.ResolveColumnOrdinal(query.selection_);

            var childout = queryRef_.AllColumnsRefs();
            output_ = CloneFixColumnOrdinal(reqOutput, childout);

            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = queryRef_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicGet<T> : LogicNode where T : TableRef
    {
        public T tabref_;

        public LogicGet(T tab) => tabref_ = tab;
        public override string ToString() => tabref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();
        public override int GetHashCode()
        {
            return base.GetHashCode() ^ (filter_?.GetHashCode()??0) ^ tabref_.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicGet<T> lo)
                return base.Equals(lo) && (filter_?.Equals(lo.filter_)??true) && tabref_.Equals(lo.tabref_);
            return false;
        }

        public bool AddFilter(Expr filter)
        {
            filter_ = FilterHelper.AddAndFilter(filter_, filter);
            return true;
        }

        void validateReqOutput(List<Expr> reqOutput)
        {
            reqOutput.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    switch (y)
                    {
                        case LiteralExpr ly:    // select 2+3, ...
                        case SubqueryExpr sy:   // select ..., sx = (select b1 from b limit 1) from a;
                            break;
                        default:
                            // aggfunc shall never pushed to me
                            Debug.Assert(!(y is AggFunc));

                            // a single table column ref, or combination of them say "c1+c2+7"
                            Debug.Assert(y.EqualTableRef(tabref_));
                            break;
                    }
                });
            });
        }
        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            List<Expr> columns = tabref_.AllColumnsRefs();

            // Verify it can be an litral, or only uses my tableref
            validateReqOutput(reqOutput);

            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, columns);
            output_ = CloneFixColumnOrdinal(reqOutput, columns);

            // Finally, consider outerrefs to this table: if they are not there, add them
            output_ = tabref_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicScanTable : LogicGet<BaseTableRef>
    {
        public LogicScanTable(BaseTableRef tab) : base(tab) { }
    }

    public class LogicScanFile : LogicGet<ExternalTableRef>
    {
        public string FileName() => tabref_.filename_;
        public LogicScanFile(ExternalTableRef tab) : base(tab) { }
    }

    public class LogicInsert : LogicNode
    {
        public BaseTableRef targetref_;
        public LogicInsert(BaseTableRef targetref, LogicNode child)
        {
            targetref_ = targetref;
            children_.Add(child);
        }
        public override string ToString() => targetref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            Debug.Assert(output_.Count == 0);

            // insertion is always the top node 
            Debug.Assert(!removeRedundant);
            return children_[0].ResolveColumnOrdinal(reqOutput, removeRedundant);
        }
    }

    public class LogicResult : LogicNode
    {
        public override string ToString() => string.Join(",", output_);
        public LogicResult(List<Expr> exprs) => output_ = exprs;
    }
}
