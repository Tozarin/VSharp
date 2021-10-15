namespace VSharp

open System
open System.IO
open System.Reflection
open System.Xml.Serialization

[<CLIMutable>]
[<Serializable>]
[<XmlInclude(typeof<structureRepr>)>]
[<XmlInclude(typeof<arrayRepr>)>]
[<XmlInclude(typeof<referenceRepr>)>]
[<XmlInclude(typeof<pointerRepr>)>]
type testInfo = {
    assemblyName : string
    moduleFullyQualifiedName : string
    token : int32
    thisArg : obj
    args : obj array
    expectedResult : obj
    throwsException : typeRepr
    memory : memoryRepr
    extraAssemblyLoadDirs : string array
}
with
    static member OfMethod(m : MethodBase) = {
        assemblyName = m.Module.Assembly.FullName
        moduleFullyQualifiedName = m.Module.FullyQualifiedName
        token = m.MetadataToken
        thisArg = null
        args = null
        expectedResult = null
        throwsException = {assemblyName = null; moduleFullyQualifiedName = null; fullName = null}
        memory = {objects = Array.empty; types = Array.empty}
        extraAssemblyLoadDirs = Array.empty
    }

type UnitTest private (m : MethodBase, info : testInfo) =
    let memoryGraph = MemoryGraph(info.memory)
    let exceptionInfo = info.throwsException
    let throwsException =
        if exceptionInfo = {assemblyName = null; moduleFullyQualifiedName = null; fullName = null} then null
        else Serialization.decodeType exceptionInfo
    let thisArg = memoryGraph.DecodeValue info.thisArg
    let args = if info.args = null then null else info.args |> Array.map memoryGraph.DecodeValue
    let expectedResult = memoryGraph.DecodeValue info.expectedResult
    let mutable extraAssemblyLoadDirs : string list = []
    new(m : MethodBase) =
        UnitTest(m, testInfo.OfMethod m)

    member x.Method with get() = m
    member x.ThisArg
        with get() = thisArg
        and set this =
            let t = typeof<testInfo>
            let p = t.GetProperty("thisArg")
            p.SetValue(info, memoryGraph.Encode this)
    member x.Args with get() = args
    member x.Expected
        with get() = expectedResult
        and set r =
            let t = typeof<testInfo>
            let p = t.GetProperty("expectedResult")
            p.SetValue(info, r)
    member x.Exception
        with get() = throwsException
        and set (e : Type) =
            let t = typeof<testInfo>
            let p = t.GetProperty("throwsException")
            let v = Serialization.encodeType e
            p.SetValue(info, v)

    member x.MemoryGraph with get() = memoryGraph

    member x.ExtraAssemblyLoadDirs with get() = info.extraAssemblyLoadDirs

    member x.AddArg (arg : ParameterInfo) (value : obj) =
        if info.args = null then
            let t = typeof<testInfo>
            let p = t.GetProperty("args")
            p.SetValue(info, Array.zeroCreate <| m.GetParameters().Length)
        let value = memoryGraph.Encode value
        info.args.[arg.Position] <- value

    member x.AddExtraAssemblySearchPath path =
        if not <| List.contains path extraAssemblyLoadDirs then
            extraAssemblyLoadDirs <- path::extraAssemblyLoadDirs

    member x.Serialize(destination : string) =
        memoryGraph.Serialize info.memory
        let t = typeof<testInfo>
        let p = t.GetProperty("extraAssemblyLoadDirs")
        p.SetValue(info, Array.ofList extraAssemblyLoadDirs)
        let serializer = XmlSerializer t
        use stream = new FileStream(destination, FileMode.OpenOrCreate, FileAccess.Write)
        serializer.Serialize(stream, info)

    static member Deserialize(stream : FileStream) =
        let serializer = XmlSerializer(typeof<testInfo>)
        try
            let ti = serializer.Deserialize(stream) :?> testInfo
            let mdle = Reflection.resolveModule ti.assemblyName ti.moduleFullyQualifiedName
            if mdle = null then raise <| InvalidOperationException(sprintf "Could not resolve module %s!" ti.moduleFullyQualifiedName)
            let method = mdle.ResolveMethod(ti.token)
            if mdle = null then raise <| InvalidOperationException(sprintf "Could not resolve method %d!" ti.token)
            UnitTest(method, ti)
        with child ->
            let exn = InvalidDataException("Input test is incorrect", child)
            raise exn

    static member Deserialize(source : string) =
        use stream = new FileStream(source, FileMode.Open, FileAccess.Read)
        UnitTest.Deserialize stream
