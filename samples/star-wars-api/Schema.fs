namespace FSharp.Data.GraphQL.Samples.StarWarsApi

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

type Root(ctx : HttpContext) =

    member _.RequestId = ctx.TraceIdentifier
    member _.RequestAborted: System.Threading.CancellationToken = ctx.RequestAborted
    member _.ServiceProvider: IServiceProvider = ctx.RequestServices
    member root.GetRequiredService<'t>() = root.ServiceProvider.GetRequiredService<'t>()

open FSharp.Data.GraphQL
open FSharp.Data.GraphQL.Types
open FSharp.Data.GraphQL.Server.Relay
open FSharp.Data.GraphQL.Server.Middleware

#nowarn "40"

type Episode =
    | NewHope = 1
    | Empire = 2
    | Jedi = 3

type Human =
    { Id : string
      Name : string option
      Friends : string list
      AppearsIn : Episode list
      HomePlanet : string option }

type Droid =
    { Id : string
      Name : string option
      Friends : string list
      AppearsIn : Episode list
      PrimaryFunction : string option }

type Planet =
    { Id : string
      Name : string option
      mutable IsMoon : bool option }

    member x.SetMoon b =
        x.IsMoon <- b
        x

type Character =
    | Human of Human
    | Droid of Droid

module Schema =

    let humans =
        [ { Id = "1000"
            Name = Some "Luke Skywalker"
            Friends = [ "1002"; "1003"; "2000"; "2001" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            HomePlanet = Some "Tatooine" }
          { Id = "1001"
            Name = Some "Darth Vader"
            Friends = [ "1004" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            HomePlanet = Some "Tatooine" }
          { Id = "1002"
            Name = Some "Han Solo"
            Friends = [ "1000"; "1003"; "2001" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            HomePlanet = None }
          { Id = "1003"
            Name = Some "Leia Organa"
            Friends = [ "1000"; "1002"; "2000"; "2001" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            HomePlanet = Some "Alderaan" }
          { Id = "1004"
            Name = Some "Wilhuff Tarkin"
            Friends = [ "1001" ]
            AppearsIn = [ Episode.NewHope ]
            HomePlanet = None } ]

    let droids =
        [ { Id = "2000"
            Name = Some "C-3PO"
            Friends = [ "1000"; "1002"; "1003"; "2001" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            PrimaryFunction = Some "Protocol" }
          { Id = "2001"
            Name = Some "R2-D2"
            Friends = [ "1000"; "1002"; "1003" ]
            AppearsIn = [ Episode.NewHope; Episode.Empire; Episode.Jedi ]
            PrimaryFunction = Some "Astromech" } ]

    let characterMap =
        seq {
            for h in humans do
                yield h.Id, Human h

            for d in droids do
                yield d.Id, Droid d
        }
        |> Map.ofSeq

    let planets =
        [ { Id = "1"
            Name = Some "Tatooine"
            IsMoon = Some false}
          { Id = "2"
            Name = Some "Endor"
            IsMoon = Some true}
          { Id = "3"
            Name = Some "Death Star"
            IsMoon = Some false} ]

    let getHuman id = humans |> List.tryFind (fun h -> h.Id = id)

    let getDroid id = droids |> List.tryFind (fun d -> d.Id = id)

    let getPlanet id = planets |> List.tryFind (fun p -> p.Id = id)

    let characters = (humans |> List.map Human) @ (droids |> List.map Droid)

    let matchesId id =
        function
        | Human h -> h.Id = id
        | Droid d -> d.Id = id

    let getCharacter id = characters |> List.tryFind (matchesId id)

    let EpisodeType =
        Define.Enum (
            name = "Episode",
            description = "One of the films in the Star Wars Trilogy.",
            options =
                [ Define.EnumValue ("NewHope", Episode.NewHope, "Released in 1977.")
                  Define.EnumValue ("Empire", Episode.Empire, "Released in 1980.")
                  Define.EnumValue ("Jedi", Episode.Jedi, "Released in 1983.") ]
        )

    let rec CharacterType =
        Define.Union (
            name = "Character",
            description = "A character in the Star Wars Trilogy.",
            options = [ HumanType; DroidType ],
            resolveValue =
                (fun o ->
                    match o with
                    | Human h -> box h
                    | Droid d -> upcast d),
            resolveType =
                (fun o ->
                    match o with
                    | Human _ -> upcast HumanType
                    | Droid _ -> upcast DroidType)
        )

    and HumanType : ObjectDef<Human> =
        Define.Object<Human> (
            name = "Human",
            description = "A humanoid creature in the Star Wars universe.",
            isTypeOf = (fun o -> o :? Human),
            fieldsFn =
                fun () ->
                    [ Define.Field ("id", StringType, "The id of the human.", (fun _ (h : Human) -> h.Id))
                      Define.Field ("name", Nullable StringType, "The name of the human.", (fun _ (h : Human) -> h.Name))
                      Define.Field (
                          "friends",
                          ConnectionOf CharacterType,
                          "The friends of the human, or an empty list if they have none.",
                          Connection.allArgs,
                          fun ctx (human: Human) ->
                              let totalCount = human.Friends.Length

                              let friends, hasNextPage =
                                  match ctx with
                                  | SliceInfo (Forward (n, after)) ->
                                      match after with
                                      | ValueSome (GlobalId ("Friend", id)) ->
                                          let i =
                                              human.Friends
                                              |> List.indexed
                                              |> List.pick (fun (i, e) -> if e = id then Some i else None)

                                          human.Friends |> List.skip (i + 1) |> List.take n, i + 1 + n < totalCount
                                      | ValueNone -> human.Friends |> List.take n, n < totalCount
                                      | _ -> failwithf "Cursor %A is not a Friend's global id" after
                                  | _ -> human.Friends, false

                              let edges =
                                  friends
                                  |> Seq.map (fun b -> { Cursor = toGlobalId "Friend" (string b); Node = characterMap[b] })
                                  |> Seq.toList

                              let headCursor =
                                  edges
                                  |> List.tryHead
                                  |> Option.map (fun edge -> edge.Cursor)

                              let pi =
                                  { HasNextPage = async { return hasNextPage }
                                    EndCursor = async { return headCursor }
                                    StartCursor = async { return None }
                                    HasPreviousPage = async { return false } }

                              let con = { TotalCount = async { return Some totalCount }; PageInfo = pi; Edges = async { return edges } }
                              con
                      )
                      Define.Field ("appearsIn", ListOf EpisodeType, "Which movies they appear in.", (fun _ (h : Human) -> h.AppearsIn))
                      Define.Field ("homePlanet", Nullable StringType, "The home planet of the human, or null if unknown.", (fun _ h -> h.HomePlanet)) ]
        )

    and DroidType =
        Define.Object<Droid> (
            name = "Droid",
            description = "A mechanical creature in the Star Wars universe.",
            isTypeOf = (fun o -> o :? Droid),
            fieldsFn =
                fun () ->
                    [ Define.Field ("id", StringType, "The id of the droid.", (fun _ (d : Droid) -> d.Id))
                      Define.Field ("name", Nullable StringType, "The name of the Droid.", (fun _ (d : Droid) -> d.Name))
                      Define
                          .Field(
                              "friends",
                              ListOf (Nullable CharacterType),
                              "The friends of the Droid, or an empty list if they have none.",
                              fun _ (d : Droid) -> d.Friends |> List.map getCharacter |> List.toSeq
                          )
                          .WithQueryWeight (0.5)
                      Define.Field ("appearsIn", ListOf EpisodeType, "Which movies they appear in.", (fun _ d -> d.AppearsIn))
                      Define.Field ("primaryFunction", Nullable StringType, "The primary function of the droid.", (fun _ d -> d.PrimaryFunction)) ]
        )

    and PlanetType =
        Define.Object<Planet> (
            name = "Planet",
            description = "A planet in the Star Wars universe.",
            isTypeOf = (fun o -> o :? Planet),
            fieldsFn =
                fun () ->
                    [ Define.Field ("id", StringType, "The id of the planet", (fun _ p -> p.Id))
                      Define.Field ("name", Nullable StringType, "The name of the planet.", (fun _ p -> p.Name))
                      Define.Field ("isMoon", Nullable BooleanType, "Is that a moon?", (fun _ p -> p.IsMoon)) ]
        )

    and RootType =
        Define.Object<Root> (
            name = "Root",
            description = "The Root type to be passed to all our resolvers.",
            isTypeOf = (fun o -> o :? Root),
            fieldsFn = fun () -> [ Define.Field ("requestId", StringType, "The ID of the client.", (fun _ (r : Root) -> r.RequestId)) ]
        )

    let Query =
        let inputs = [ Define.Input ("id", StringType) ]
        Define.Object<Root> (
            name = "Query",
            fields =
                [ Define.Field ("hero", Nullable HumanType, "Gets human hero", inputs, fun ctx _ -> getHuman (ctx.Arg ("id")))
                  Define.Field ("droid", Nullable DroidType, "Gets droid", inputs, (fun ctx _ -> getDroid (ctx.Arg ("id"))))
                  Define.Field ("planet", Nullable PlanetType, "Gets planet", inputs, fun ctx _ -> getPlanet (ctx.Arg ("id")))
                  Define.Field ("characters", ListOf CharacterType, "Gets characters", (fun _ _ -> characters)) ]
        )

    let Subscription =
        Define.SubscriptionObject<Root> (
            name = "Subscription",
            fields =
                [ Define.SubscriptionField (
                      "watchMoon",
                      RootType,
                      PlanetType,
                      "Watches to see if a planet is a moon.",
                      [ Define.Input ("id", StringType) ],
                      (fun ctx _ p -> if ctx.Arg ("id") = p.Id then Some p else None)
                  ) ]
        )

    let schemaConfig = SchemaConfig.Default

    let Mutation =
        Define.Object<Root> (
            name = "Mutation",
            fields =
                [ Define.Field (
                      "setMoon",
                      Nullable PlanetType,
                      "Defines if a planet is actually a moon or not.",
                      [ Define.Input ("id", StringType); Define.Input ("isMoon", BooleanType) ],
                      fun ctx _ ->
                          getPlanet (ctx.Arg ("id"))
                          |> Option.map (fun x ->
                              x.SetMoon (Some (ctx.Arg ("isMoon"))) |> ignore
                              schemaConfig.SubscriptionProvider.Publish<Planet> "watchMoon" x
                              schemaConfig.LiveFieldSubscriptionProvider.Publish<Planet> "Planet" "isMoon" x
                              x)
                  ) ]
        )

    let schema : ISchema<Root> = upcast Schema (Query, Mutation, Subscription, schemaConfig)

    let middlewares =
        [ Define.QueryWeightMiddleware (2.0, true)
          Define.ObjectListFilterMiddleware<Human, Character option> (true)
          Define.ObjectListFilterMiddleware<Droid, Character option> (true)
          Define.LiveQueryMiddleware () ]

    let executor = Executor (schema, middlewares)
