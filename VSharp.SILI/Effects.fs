﻿namespace VSharp

open System.Collections.Generic

module Effects =

    type internal ConcreteHeapAddress = int list
    type internal SymbolicEffectContext = { state : State.state; address : ConcreteHeapAddress; time : Timestamp }

    type private SymbolicEffectSource(funcId : FunctionIdentifier, ctx : SymbolicEffectContext) =
        inherit SymbolicConstantSource()
        override x.SubTerms = Seq.empty
        member x.Id = funcId
        member x.Context = ctx

    type private ReturnSymbolicEffectSource(funcId : FunctionIdentifier, ctx : SymbolicEffectContext) =
        inherit SymbolicEffectSource(funcId, ctx)

    type private MutationtSymbolicEffectSource(funcId : FunctionIdentifier, ctx : SymbolicEffectContext, uloc : Term, oloc : Term) =
        inherit SymbolicEffectSource(funcId, ctx)
        member x.UltimateLocation = uloc
        member x.OriginalLocation = oloc

    type private FreshAddressMarker() = class end
    let private freshAddressMarker = FreshAddressMarker()

    let private convergedEffects = new HashSet<FunctionIdentifier>()
    let private returnValues = new Dictionary<FunctionIdentifier, StatementResult>()
    let private mutations = new Dictionary<FunctionIdentifier, IDictionary<Term, MemoryCell<Term>>>()

    let private isFrozen = convergedEffects.Contains >> not


    let private composeAddresses (a1 : ConcreteHeapAddress) (a2 : ConcreteHeapAddress) =
        List.append a1 a2

    let private composeTime (t1 : Timestamp) (t2 : Timestamp) =
        // TODO
        t1

    let rec private fillInHole mtd ctx term =
        match term.term with
        | Constant(_, source, _) ->
            match source with
            | Memory.LazyInstantiation(loc, isTop) ->
                match Memory.derefIfInstantiated ctx.state loc with
                | Some result -> if isTop then Pointers.topLevelLocation result else result
                | None -> term
            | :? SymbolicEffectSource as e ->
                apply e mtd ctx
            | _ -> term
        | Concrete(:? ConcreteHeapAddress as addr, t) when term.metadata.misc.Contains(freshAddressMarker) ->
            Concrete mtd (composeAddresses ctx.address addr) t
        | _ -> term

    and private fillInHoles mtd ctx term =
        Common.substitute (fillInHole mtd ctx) term

    and private apply (src : SymbolicEffectSource) mtd prefix =
        let composedCtx = compose mtd prefix src.Context in
        let pattern =
            match src with
            | :? MutationtSymbolicEffectSource as src ->
                assert(mutations.ContainsKey(src.Id) && mutations.[src.Id].ContainsKey(src.OriginalLocation))
                mutations.[src.Id].[src.OriginalLocation] |> fst3
            | :? ReturnSymbolicEffectSource as src ->
                assert(returnValues.ContainsKey(src.Id))
                returnValues.[src.Id] |> ControlFlow.resultToTerm
            | _ -> __unreachable__()
        in fillInHoles mtd composedCtx pattern

    and private composeStates mtd (ctx : SymbolicEffectContext) (state : State.state) =
        let fillAndMutate s k v =
            let k = fillInHoles mtd ctx k in
            let v = fillInHoles mtd ctx (fst3 v) in
            Memory.mutate mtd s k v |> snd
        in Memory.fold fillAndMutate ctx.state state

    and private compose mtd prefix x =
        { state = composeStates mtd prefix x.state; address = composeAddresses prefix.address x.address; time = composeTime prefix.time x.time }

    type private SymbolicEffectSource with
        member x.Apply mtd prefix = apply x mtd prefix

    let private produceFrozenReturnValue mtd id ctx =
        let effectName = toString id + "!!ret" in
        Constant mtd effectName (ReturnSymbolicEffectSource(id, ctx)) (Types.ReturnType id) |> Return mtd

    let private produceUnfrozenReturnValue mtd id ctx =
        assert(returnValues.ContainsKey(id))
        fillInHoles mtd ctx (ControlFlow.resultToTerm returnValues.[id]) |> ControlFlow.throwOrReturn

    let private produceReturnValue mtd id ctx =
        if isFrozen id then produceFrozenReturnValue mtd id ctx
        else produceUnfrozenReturnValue mtd id ctx

    let private produceFrozenEffect mtd id ctx ptr origPtr value =
        let effectName = sprintf "%O!!%O!!eff" id ptr in
        Constant mtd effectName (MutationtSymbolicEffectSource(id, ctx, ptr, origPtr)) (TypeOf value) in

    let private produceEffect mtd id ctx (kvp : KeyValuePair<Term, MemoryCell<Term>>) =
        let ptr = fillInHoles mtd ctx kvp.Key in
        let effect =
            if isFrozen id then produceFrozenEffect mtd id ctx ptr kvp.Key (fst3 kvp.Value)
            else fillInHoles mtd ctx (fst3 kvp.Value)
        in (ptr, effect)

    let internal invoke mtd id ctx k =
        let returnValue = produceReturnValue mtd id ctx in
        let effects =
            if mutations.ContainsKey(id) then
                mutations.[id] |> Seq.map (produceEffect mtd id ctx)
            else Seq.empty
        in
        let state = Seq.fold (fun state (ptr, value) -> Memory.mutate mtd state ptr value |> snd) ctx.state effects in
        k (returnValue, state)

    let private produceFreshAddressEffect = function
        | ConcreteT(:? ConcreteHeapAddress, _) as t -> Metadata.addMisc t freshAddressMarker
        | t -> ()

    let internal defaultMemoryFilter reference (src : SymbolicConstantSource) =
        match src with
        | Memory.LazyInstantiation(reference', _) -> reference = reference'
        | :? MutationtSymbolicEffectSource as es -> es.UltimateLocation = reference
        | _ -> false

    let internal parseEffects mtd id startTime result state =
        let freshLocations, mutatedLocations = Memory.affectedLocations defaultMemoryFilter startTime state in
        // TODO: time!
        let markFresh = Terms.iter produceFreshAddressEffect in
        freshLocations |> Seq.iter (fun (k, (v, _, _)) -> markFresh k; markFresh v)
        mutatedLocations |> Seq.iter (fun (k, (v, _, _)) -> markFresh k; markFresh v)
        result |> ControlFlow.resultToTerm |> markFresh
        let effects = List.append freshLocations mutatedLocations |> Dict.ofSeq in
        let resultsConverged = returnValues.ContainsKey(id) && returnValues.[id] = result in
        let effectsConverged = mutations.ContainsKey(id) && Dict.equals mutations.[id] effects in
        returnValues.[id] <- result
        mutations.[id] <- effects
        resultsConverged && effectsConverged && convergedEffects.Add(id)
