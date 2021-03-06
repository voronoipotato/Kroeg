using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kroeg.ActivityStreams;
using Kroeg.Server.Configuration;
using Kroeg.Server.Middleware.Handlers;
using Kroeg.Server.Middleware.Handlers.ClientToServer;
using Kroeg.Server.Middleware.Handlers.ServerToServer;
using Kroeg.Server.Middleware.Handlers.Shared;
using Kroeg.Server.Models;
using Kroeg.Server.OStatusCompat;
using Kroeg.Server.Salmon;
using Kroeg.Server.Services;
using Kroeg.Server.Services.EntityStore;
using Kroeg.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Kroeg.Server.Middleware.Renderers;
using System.Threading;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Microsoft.Extensions.Primitives;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Transactions;
using System.Data.Common;
using Kroeg.Server.BackgroundTasks;
using Npgsql;

// For more information on enabling MVC for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace Kroeg.Server.Middleware
{
    public class GetEntityMiddleware
    {
        private readonly RequestDelegate _next;
        private List<IConverterFactory> _converters;

        public GetEntityMiddleware(RequestDelegate next)
        {
            _next = next;
            _converters = new List<IConverterFactory>
            {
                new AS2ConverterFactory(),
                new SalmonConverterFactory(),
                new AtomConverterFactory(true)
            };
        }

        public async Task Invoke(HttpContext context, IServiceProvider serviceProvider, EntityData entityData, IEntityStore store)
        {
            var handler = ActivatorUtilities.CreateInstance<GetEntityHandler>(serviceProvider, context.User);
            if (entityData.RewriteRequestScheme) context.Request.Scheme = "https";

            var fullpath = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            foreach (var converterFactory in _converters)
            {
                if (converterFactory.FileExtension != null && fullpath.EndsWith("." + converterFactory.FileExtension))
                {
                    fullpath = fullpath.Substring(0, fullpath.Length - 1 - converterFactory.FileExtension.Length);
                    context.Request.Headers.Remove("Accept");
                    context.Request.Headers.Add("Accept", converterFactory.RenderMimeType);
                    break;
                }
            }

            if (!context.Request.Path.ToUriComponent().StartsWith("/auth"))
            {
                context.Response.Headers.Add("Access-Control-Allow-Credentials", "true");
                context.Response.Headers.Add("Access-Control-Allow-Headers", "Authorization");
                context.Response.Headers.Add("Access-Control-Expose-Headers", "Link");
                context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST");
                context.Response.Headers.Add("Access-Control-Allow-Origin", context.Request.Headers["Origin"]);
                context.Response.Headers.Add("Vary", "Origin");
            }

            /* && ConverterHelpers.GetBestMatch(_converters[0].MimeTypes, context.Request.Headers["Accept"]) != null */
            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 200;
                return;
            }

            Console.WriteLine(fullpath);
            foreach (var line in context.Request.Headers["Accept"]) Console.WriteLine($"---- {line}");

            if (context.Request.Headers["Accept"].Contains("text/event-stream"))
            {
                await handler.EventStream(context, fullpath);
                return;
            }


            if (context.WebSockets.IsWebSocketRequest)
            {
                await handler.WebSocket(context, fullpath);
                return;
            }

            if (context.Request.Method == "POST" && context.Request.ContentType.StartsWith("multipart/form-data"))
            {
                context.Items.Add("fullPath", fullpath);
                context.Request.Path = "/settings/uploadMedia";
                await _next(context);
                return;
            }

            if (context.Request.QueryString.Value == "?hub")
            {
                context.Items.Add("fullPath", fullpath);
                context.Request.Path = "/.well-known/hub";
                context.Request.QueryString = QueryString.Empty;
                await _next(context);
                return;
            }

            IConverter readConverter = null;
            IConverter writeConverter = null;
            bool needRead = context.Request.Method == "POST";
            var target = fullpath;
            APEntity targetEntity = null;
            targetEntity = await store.GetEntity(target, false);

            if (needRead)
            {
                if (targetEntity?.Type == "_inbox")
                    target = targetEntity.Data["attributedTo"].Single().Id;
            }

            if (targetEntity == null)
            {
                await _next(context);
                return;
            }


            var acceptHeaders = context.Request.Headers["Accept"];
            if (acceptHeaders.Count == 0 && context.Request.ContentType != null)
            {
                acceptHeaders.Append(context.Request.ContentType);
            }

            foreach (var converterFactory in _converters)
            {
                bool worksForWrite = converterFactory.CanRender && ConverterHelpers.GetBestMatch(converterFactory.MimeTypes, acceptHeaders) != null; 
                bool worksForRead = needRead && converterFactory.CanParse && ConverterHelpers.GetBestMatch(converterFactory.MimeTypes, context.Request.ContentType) != null;

                if (worksForRead && worksForWrite && readConverter == null && writeConverter == null)
                {
                    readConverter = writeConverter = converterFactory.Build(serviceProvider, target);
                    break;
                }

                if (worksForRead && readConverter == null)
                    readConverter = converterFactory.Build(serviceProvider, target);

                if (worksForWrite && writeConverter == null)
                    writeConverter = converterFactory.Build(serviceProvider, target);
            }

            ASObject data = null;
            if (readConverter != null)
                data = await readConverter.Parse(context.Request.Body);

            if (needRead && readConverter != null && writeConverter == null) writeConverter = readConverter;

            if (data == null && needRead && targetEntity != null)
            {
                context.Response.StatusCode = 415;
                await context.Response.WriteAsync("Unknown mime type " + context.Request.ContentType);
                return;
            }

            var arguments = context.Request.Query;
            APEntity entOut = null;

            try
            {
                if (context.Request.Method == "GET" || context.Request.Method == "HEAD" || context.Request.Method == "OPTIONS")
                {
                    entOut = await handler.Get(fullpath, arguments, context, targetEntity);
                    if (entOut?.Id == "kroeg:unauthorized" && writeConverter != null) throw new UnauthorizedAccessException("no access");
                }
                else if (context.Request.Method == "POST" && data != null)
                {
                    await handler._connection.OpenAsync();
                    using (var transaction = handler._connection.BeginTransaction())
                    {
                        entOut = await handler.Post(context, fullpath, targetEntity, data);
                        transaction.Commit();
                    }
                }
            }
            catch (UnauthorizedAccessException e)
            {
                context.Response.StatusCode = 403;
                Console.WriteLine(e);
                await context.Response.WriteAsync(e.Message);
            }
            catch (InvalidOperationException e)
            {
                context.Response.StatusCode = 401;
                Console.WriteLine(e);
                await context.Response.WriteAsync(e.Message);
            }

            if (context.Response.HasStarted)
                return;

            if (entOut != null)
            {

                if (context.Request.Method == "HEAD")
                {
                    context.Response.StatusCode = 200;
                    return;
                }

                if (writeConverter != null)
                    await writeConverter.Render(context.Request, context.Response, entOut);
                else if (context.Request.ContentType == "application/magic-envelope+xml")
                {
                    context.Response.StatusCode = 202;
                    await context.Response.WriteAsync("accepted");
                }
                else
                {
                    context.Request.Method = "GET";
                    context.Request.Path = "/render";
                    context.Items["object"] = entOut;
                    await _next(context);
                }
                return;
            }

            if (!context.Response.HasStarted)
            {
                await _next(context);
            }
        }
        public class GetEntityHandler
        {
            internal readonly DbConnection _connection;
            private readonly EntityFlattener _flattener;
            private readonly IEntityStore _mainStore;
            private readonly AtomEntryGenerator _entryGenerator;
            private readonly IServiceProvider _serviceProvider;
            private readonly EntityData _entityData;
            private readonly DeliveryService _deliveryService;
            private readonly ClaimsPrincipal _user;
            private readonly CollectionTools _collectionTools;
            private readonly INotifier _notifier;
            private readonly JwtTokenSettings _tokenSettings;
            private readonly SignatureVerifier _verifier;
            private readonly IAuthorizer _authorizer;

            public GetEntityHandler(DbConnection connection, EntityFlattener flattener, IEntityStore mainStore,
                AtomEntryGenerator entryGenerator, IServiceProvider serviceProvider, DeliveryService deliveryService,
                EntityData entityData, ClaimsPrincipal user, CollectionTools collectionTools, INotifier notifier, JwtTokenSettings tokenSettings,
                SignatureVerifier verifier, IAuthorizer authorizer)
            {
                _connection = connection;
                _flattener = flattener;
                _mainStore = mainStore;
                _entryGenerator = entryGenerator;
                _serviceProvider = serviceProvider;
                _entityData = entityData;
                _deliveryService = deliveryService;
                _user = user;
                _collectionTools = collectionTools;
                _notifier = notifier;
                _tokenSettings = tokenSettings;
                _verifier = verifier;
                _authorizer = authorizer;
            }

            internal async Task<APEntity> Get(string url, IQueryCollection arguments, HttpContext context, APEntity existing)
            {
                var userId = _user.FindFirstValue(JwtTokenSettings.ActorClaim);
                var entity = existing ?? await _mainStore.GetEntity(url, false);
                if (entity == null) return null;
                if (userId == null) userId = await _verifier.Verify(url, context);
                if (!await _authorizer.VerifyAccess(entity, userId))
                {
                    var unauth = new ASObject();
                    unauth.Id = "kroeg:unauthorized";
                    unauth.Type.Add("kroeg:Unauthorized");

                    return APEntity.From(unauth);
                }
                if (entity.Type == "https://www.w3.org/ns/activitystreams#OrderedCollection"
                    || entity.Type == "https://www.w3.org/ns/activitystreams#Collection"
                    || entity.Type.StartsWith("_"))
                {
                    if (entity.IsOwner && !entity.Data["totalItems"].Any())
                        try {
                            return APEntity.From(await _getCollection(entity, arguments), true);
                        } catch (FormatException) {
                            throw new InvalidOperationException("Invalid parameters!");
                        }
                    else
                        return (await _mainStore.GetEntity(url + context.Request.QueryString.Value, true)) ?? entity;
                } 

                return entity;
            }

            public async Task EventStream(HttpContext context, string fullpath)
            {
                var entity = await _mainStore.GetEntity(fullpath, false);
                var authToken = context.Request.Query["authorization"].Concat(context.Request.Headers["Authorization"].Select(a => a.Split(' ')[1])).ToList();
                if (authToken.Count == 0)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("No authorization token provided");
                    return;
                }

                var tokenHandler = new JwtSecurityTokenHandler();
                var claims = tokenHandler.ValidateToken(authToken[0], _tokenSettings.ValidationParameters, out SecurityToken validatedToken);
                var entityClaim = claims.FindFirstValue(JwtTokenSettings.ActorClaim);
                if (entityClaim == null) return;
                if (entity.Data["attributedTo"].TrueForAll(a => a.Id != entityClaim))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("Not authorized to view this item live");
                    return;
                }

                if ((!entity.Type.StartsWith("_") && entity.Type != "OrderedCollection") || !entity.IsOwner)
                {
                    context.Response.StatusCode = 415;
                    await context.Response.WriteAsync("Cannot view this item live!");
                    return;
                }


                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("X-Accel-Buffering", "no");
                var tokenSource = new CancellationTokenSource();
                ConcurrentQueue<string> toSend = new ConcurrentQueue<string>();

                Action<string> subscriptionCall = (item) =>
                {
                    toSend.Enqueue(item);
                    tokenSource.Cancel();
                };

                await _notifier.Subscribe($"collection/{fullpath}", subscriptionCall);
                context.RequestAborted.Register(() => tokenSource.Cancel());

                await context.Response.WriteAsync(": hello, world!\n");
                await context.Response.Body.FlushAsync();

                // todo: Last-Event-Id

                while (true)
                {
                    if (toSend.Count == 0)
                    {
                        await context.Response.WriteAsync(":keepalive\n");
                        await context.Response.Body.FlushAsync();
                    }
                    else
                    {
                        do
                        {
                            var success = toSend.TryDequeue(out var item);
                            if (success)
                            {
                                var stored = await _mainStore.GetEntity(item, false);
                                var unflattened = await _flattener.Unflatten(_mainStore, stored);
                                var serialized = unflattened.Serialize(true).ToString(Formatting.None);
                                await context.Response.WriteAsync($"id: {item}\ndata: {serialized}\n\n");
                                await context.Response.Body.FlushAsync();
                            }
                        } while (toSend.Count > 0);
                    }
                    try
                    {
                        await ((NpgsqlConnection)_connection).WaitAsync(tokenSource.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        if (context.RequestAborted.IsCancellationRequested) break;
                    }

                    tokenSource.Dispose();
                    tokenSource = new CancellationTokenSource();
                }

                await _notifier.Unsubscribe($"collection/{fullpath}", subscriptionCall);
                context.Response.Body.Dispose();
            }

            public async Task WebSocket(HttpContext context, string fullpath)
            {

                var entity = await _mainStore.GetEntity(fullpath, false);
                var authToken = context.Request.Query["authorization"].Concat(context.Request.Headers["Authorization"].Select(a => a.Split(' ')[1])).ToList();
                if (authToken.Count == 0)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("No authorization token provided");
                    return;
                }

                var tokenHandler = new JwtSecurityTokenHandler();
                var claims = tokenHandler.ValidateToken(authToken[0], _tokenSettings.ValidationParameters, out SecurityToken validatedToken);
                var entityClaim = claims.FindFirstValue(JwtTokenSettings.ActorClaim);
                if (entityClaim == null) return;
                if (entity.Data["attributedTo"].TrueForAll(a => a.Id != entityClaim))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("Not authorized to view this item live");
                    return;
                }

                if ((!entity.Type.StartsWith("_") && entity.Type != "OrderedCollection") || !entity.IsOwner)
                {
                    context.Response.StatusCode = 415;
                    await context.Response.WriteAsync("Cannot view this item live!");
                    return;
                }

                if (context.WebSockets.WebSocketRequestedProtocols.Count > 0 && !context.WebSockets.WebSocketRequestedProtocols.Contains("activitypub"))
                {
                    context.Response.StatusCode = 415;
                    await context.Response.WriteAsync("Invalid protocol?");
                    return;
                }

                var tokenSource = new CancellationTokenSource();
                ConcurrentQueue<string> toSend = new ConcurrentQueue<string>();

                Action<string> subscriptionCall = (item) =>
                {
                    toSend.Enqueue(item);
                    tokenSource.Cancel();
                };

                await _notifier.Subscribe($"collection/{fullpath}", subscriptionCall);
                context.RequestAborted.Register(() => tokenSource.Cancel());

                WebSocket socket;
                if (context.WebSockets.WebSocketRequestedProtocols.Count > 0)
                    socket = await context.WebSockets.AcceptWebSocketAsync("activitypub");
                else
                    socket = await context.WebSockets.AcceptWebSocketAsync();
                var buf = new byte[1024];
                var seg = new ArraySegment<byte>(buf);

                while (true)
                {
                    try
                    {
                        tokenSource.CancelAfter(30000);
                        await ((NpgsqlConnection)_connection).WaitAsync(tokenSource.Token);
                        var b = new ArraySegment<byte>(new byte[] { });
                        await socket.SendAsync(b, WebSocketMessageType.Text, false, CancellationToken.None);
                    } catch (TaskCanceledException) { }

                    if (socket.State != WebSocketState.Open) break;

                    do
                    {
                        var success = toSend.TryDequeue(out var item);
                        if (success)
                        {
                            var stored = await _mainStore.GetEntity(item, false);

                            var unflattened = await _flattener.Unflatten(_mainStore, stored);
                            var serialized = Encoding.UTF8.GetBytes(unflattened.Serialize().ToString(Formatting.None));
                            var segment = new ArraySegment<byte>(serialized);
                            await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    } while (toSend.Count > 0);

                    tokenSource.Dispose();
                    tokenSource = new CancellationTokenSource();
                }

                await socket.CloseAsync(WebSocketCloseStatus.Empty, "other side closed", CancellationToken.None);
                await _notifier.Unsubscribe($"collection/{fullpath}", subscriptionCall);
            }

            private async Task<ASObject> _getCollection(APEntity entity, IQueryCollection arguments)
            {
                var from_id = arguments["from_id"].FirstOrDefault();
                var to_id = arguments["to_id"].FirstOrDefault();
                var collection = entity.Data;
                bool seePrivate = collection["attributedTo"].Any() && _user.FindFirstValue(JwtTokenSettings.ActorClaim) == collection["attributedTo"].First().Id;

                if (from_id != null || to_id != null)
                {
                    var fromId = from_id != null ? int.Parse(from_id) : int.MaxValue;
                    var toId = to_id != null ? int.Parse(to_id) : int.MinValue;
                    var maxitem = (await _collectionTools.GetItems(entity.Id, count: 1)).FirstOrDefault()?.CollectionItemId;
                    var items = await _collectionTools.GetItems(entity.Id, fromId, toId, count: 11);
                    var hasItems = items.Any();
                    var page = new ASObject();
                    page.Type.Add("https://www.w3.org/ns/activitystreams#OrderedCollectionPage");
                    page["summary"].Add(ASTerm.MakePrimitive("A collection"));
                    page.Id = entity.Id + "?from_id=" + (hasItems ? fromId : 0);
                    page["partOf"].Add(ASTerm.MakeId(entity.Id));
                    if (collection["attributedTo"].Any())
                        page["attributedTo"].Add(collection["attributedTo"].First());
                    if (items.Count > 0 && items[0].CollectionItemId != maxitem)
                        page["prev"].Add(ASTerm.MakeId(entity.Id + "?to_id=" + items[0].CollectionItemId.ToString()));
                    if (items.Count > 10)
                        page["next"].Add(ASTerm.MakeId(entity.Id + "?from_id=" + (items[9].CollectionItemId - 1).ToString()));
                    page["orderedItems"].AddRange(items.Take(10).Select(a => ASTerm.MakeId(a.Entity.Id)));

                    return page;
                }
                else
                {
                    var items = await _collectionTools.GetItems(entity.Id, count: 1);
                    var hasItems = items.Any();
                    var page = entity.Id + "?from_id=" + (hasItems ? items.First().CollectionItemId + 1 : 0);
                    collection["current"].Add(ASTerm.MakeId(entity.Id));
                    collection["totalItems"].Add(ASTerm.MakePrimitive(await _collectionTools.Count(entity.Id), ASTerm.NON_NEGATIVE_INTEGER));
                    collection["first"].Add(ASTerm.MakeId(page));
                    return collection;
                }
            }

            internal async Task<APEntity> Post(HttpContext context, string fullpath, APEntity original, ASObject @object)
            {
                if (!original.IsOwner) return null;

                switch (original.Type)
                {
                    case "_inbox":
                        var actorObj = @object["actor"].First();
                        string subjectId = actorObj.Id ?? actorObj.SubObject.Id;
                        subjectId = await _verifier.Verify(fullpath, context) ?? subjectId;
                        if (subjectId == null) throw new UnauthorizedAccessException("Invalid signature");
                        return await ServerToServer(original, @object, subjectId);
                    case "_outbox":
                        var userId = original.Data["attributedTo"].FirstOrDefault() ?? original.Data["actor"].FirstOrDefault();
                        if (userId == null || _user.FindFirst(JwtTokenSettings.ActorClaim).Value == userId.Id)
                            return await ClientToServer(original, @object);
                        throw new UnauthorizedAccessException("Cannot post to the outbox of another actor");
                }

                return null;
            }

            public async Task<APEntity> ServerToServer(APEntity inbox, ASObject activity, string subject = null)
            {
                var stagingStore = new StagingEntityStore(_mainStore);
                var userId = inbox.Data["attributedTo"].Single().Id;
                var user = await _mainStore.GetEntity(userId, false);

                APEntity flattened;

                string prefix = "";
                if (subject != null)
                {
                    var subjectUri = new Uri(subject);
                    prefix = $"{subjectUri.Scheme}://{subjectUri.Host}";
                    if (!subjectUri.IsDefaultPort) prefix += $":{subjectUri.Port}";
                    prefix += "/";
                }

                var id = activity.Id;
                flattened = await _mainStore.GetEntity(id, false);
                if (flattened == null)
                    flattened = await _flattener.FlattenAndStore(stagingStore, activity, false);

                stagingStore.TrimDown(prefix); // remove all staging entities that may be faked

                var sentBy = activity["actor"].First().Id;
                if (subject != null && sentBy != subject)
                    throw new UnauthorizedAccessException("Invalid authorization header for this subject!");

                if (user.Data["blocks"].Any())
                {
                    var blocks = await _mainStore.GetEntity(user.Data["blocks"].First().Id, false);
                    var blocked = await _mainStore.GetEntity(blocks.Data["blocked"].First().Id, false);
                    if (await _collectionTools.Contains(blocked, sentBy))
                        throw new UnauthorizedAccessException("You are blocked.");
                }

                if (await _collectionTools.Contains(inbox, id))
                    return flattened;

                await stagingStore.CommitChanges();

                foreach (var type in EntityData.ServerToServerHandlers.Concat(EntityData.ExtraFilters))
                {
                    var handler = (BaseHandler)ActivatorUtilities.CreateInstance(_serviceProvider, type,
                        _mainStore, flattened, user, inbox, _user);
                    var handled = await handler.Handle();
                    flattened = handler.MainObject;
                    if (!handled) break;
                }

                return flattened;
            }

            public async Task<APEntity> ClientToServer(APEntity outbox, ASObject activity)
            {
                var stagingStore = new StagingEntityStore(_mainStore);
                var userId =  outbox.Data["attributedTo"].Single().Id;
                var user = await _mainStore.GetEntity(userId, false);

                if (!_entityData.IsActivity(activity))
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    if (_entityData.IsActivity(activity.Type.FirstOrDefault()))
#pragma warning restore CS0618 // Type or member is obsolete
                    {
                        throw new UnauthorizedAccessException("Sending an Activity without an actor isn't allowed. Are you sure you wanted to do that?");
                    }

                    var createActivity = new ASObject();
                    createActivity.Type.Add("https://www.w3.org/ns/activitystreams#Create");
                    createActivity["to"].AddRange(activity["to"]);
                    createActivity["bto"].AddRange(activity["bto"]);
                    createActivity["cc"].AddRange(activity["cc"]);
                    createActivity["bcc"].AddRange(activity["bcc"]);
                    createActivity["audience"].AddRange(activity["audience"]);
                    createActivity["actor"].Add(ASTerm.MakeId(userId));
                    createActivity["object"].Add(ASTerm.MakeSubObject(activity));
                    activity = createActivity;
                }

                if (activity.Type.Contains("https://www.w3.org/ns/activitystreams#Create"))
                {
                    activity.Id = null;
                    if (activity["object"].SingleOrDefault()?.SubObject != null)
                        activity["object"].Single().SubObject.Id = null;
                }

                var flattened = await _flattener.FlattenAndStore(stagingStore, activity);
                IEntityStore store = stagingStore;

                foreach (var type in EntityData.ClientToServerHandlers.Concat(EntityData.ExtraFilters))
                {
                    var handler = (BaseHandler)ActivatorUtilities.CreateInstance(_serviceProvider, type,
                        store, flattened, user, outbox, _user);
                    var handled = await handler.Handle();
                    flattened = handler.MainObject;
                    if (!handled) break;
                    if (type == typeof(CommitChangesHandler))
                        store = _mainStore;
                }

                return flattened;
            }
        }
    }
}
