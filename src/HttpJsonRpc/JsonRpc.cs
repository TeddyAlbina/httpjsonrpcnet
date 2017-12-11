﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HttpJsonRpc
{
    public static class JsonRpc
    {
        private static HttpListener Listener { get; set; }
        private static Dictionary<string, MethodInfo> Methods { get; } = new Dictionary<string, MethodInfo>();
        public static JsonSerializerSettings SerializerSettings { get; set; } = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        private static List<Func<JsonRpcContext, Task>> OnReceivedRequestFuncs { get; } = new List<Func<JsonRpcContext, Task>>();

        public static void OnReceivedRequest(Func<JsonRpcContext, Task> func)
        {
            OnReceivedRequestFuncs.Add(func);
        }

        public static void RegisterMethods(Assembly fromAssembly)
        {
            if (fromAssembly == null) throw new ArgumentNullException(nameof(fromAssembly));

            foreach (var t in fromAssembly.DefinedTypes)
            {
                foreach (var m in t.DeclaredMethods)
                {
                    var a = m.GetCustomAttribute<JsonRpcMethodAttribute>();
                    if (a != null)
                    {
                        var name = a.Name ?? $"{m.DeclaringType.Name}.{m.Name}";
                        var asyncIndex = name.LastIndexOf("Async", StringComparison.Ordinal);
                        if (asyncIndex > -1)
                        {
                            name = name.Remove(asyncIndex);
                        }

                        name = name.ToLowerInvariant();

                        Methods.Add(name, m);
                    }
                }
            }
        }

        public static async void Start(string address = null)
        {
            if (Methods.Count == 0)
            {
                var excludeProducts = new List<string> {"Microsoft® .NET Framework", "Json.NET", "HttpJsonRpc"};

                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !excludeProducts.Contains(a.GetCustomAttribute<AssemblyProductAttribute>()?.Product));

                foreach (var assembly in assemblies)
                {
                    RegisterMethods(assembly);
                }
            }

            if (address == null) address = "http://localhost:5000/";
            if (!address.EndsWith("/")) address += "/";

            Listener = new HttpListener();
            Listener.Prefixes.Add(address);
            Listener.Start();

            Console.WriteLine($"Listening for JSON-RPC requests on {address}");

            while (Listener.IsListening)
            {
                var httpContext = await Listener.GetContextAsync();
                HandleRequest(httpContext);
            }
        }
        
        private static async void HandleRequest(HttpListenerContext httpContext)
        {
            if (!new[] { "GET", "POST" }.Contains(httpContext.Request.HttpMethod, StringComparer.InvariantCultureIgnoreCase))
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                httpContext.Response.OutputStream.Close();
                return;
            }

            var contentType = httpContext.Request.ContentType?.ToLowerInvariant().Split(';')[0];
            if (contentType == null)
            {
                var jsonRpcMethods = GetJsonRpcMethods();
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromResult(null, jsonRpcMethods));
                return;
            }

            string requestJson = null;
            switch (contentType)
            {
                case "application/json":
                    using (var reader = new StreamReader(httpContext.Request.InputStream))
                    {
                        requestJson = await reader.ReadToEndAsync();
                    }
                    break;
                case "multipart/form-data":
                    using (var reader = new StreamReader(httpContext.Request.InputStream))
                    {
                        var boundary = await reader.ReadLineAsync();
                        var multipartString = await reader.ReadToEndAsync();
                        var parts = multipartString.Split(new[] {boundary}, StringSplitOptions.RemoveEmptyEntries);
                        var requestPartHeader = "Content-Disposition: form-data; name=\"request\"";
                        var requestPart = parts.FirstOrDefault(p => p.StartsWith(requestPartHeader));

                        if (requestPart != null)
                        {
                            requestJson = requestPart.Substring(requestPartHeader.Length).Trim();
                        }
                    }
                    break;
                default:
                    httpContext.Response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                    httpContext.Response.OutputStream.Close();
                    return;
            }
            
            JsonRpcRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRpcRequest>(requestJson);
            }
            catch (Exception e)
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.ParseError, null, e));
                return;
            }

            var jsonRpcContext = new JsonRpcContext(httpContext, request);
            JsonRpcContext.Current = jsonRpcContext;

            try
            {
                foreach (var f in OnReceivedRequestFuncs)
                {
                    await f(jsonRpcContext);
                }
            }
            catch (Exception e)
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.InternalError, request.Id, e));
                return;
            }

            var methodName = request.Method?.ToLowerInvariant() ?? string.Empty;
            if (!Methods.TryGetValue(methodName, out var method))
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.MethodNotFound, request.Id, method));
                return;
            }

            var parameterValues = new List<object>();
            try
            {
                var parameters = method.GetParameters();

                foreach (var parameter in parameters)
                {
                    var parameterAttribute = parameter.GetCustomAttribute<JsonRpcParameterAttribute>();
                    if (parameterAttribute?.Ignore == true)
                    {
                        parameterValues.Add(Type.Missing);
                        continue;
                    }

                    var parameterName = parameterAttribute?.Name ?? parameter.Name;
                    var value = request.Params?[parameterName]?.ToObject(parameter.ParameterType) ?? Type.Missing;
                    parameterValues.Add(value);
                }
            }
            catch (Exception e)
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.ParseError, request.Id, e));
                return;
            }

            try
            {
                try
                {
                    var methodTask = (Task)method.Invoke(null, parameterValues.ToArray());
                    await methodTask;
                    var result = methodTask.GetType().GetProperty("Result")?.GetValue(methodTask);

                    var response = new JsonRpcResponse
                    {
                        Id = request.Id,
                        JsonRpc = "2.0",
                        Result = result
                    };

                    await WriteResponseAsync(httpContext, response);
                    return;
                }
                catch (TargetInvocationException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
            }
            catch (JsonRpcUnauthorizedException e)
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.Unauthorized, request.Id, e));
                return;
            }
            catch (Exception e)
            {
                await WriteResponseAsync(httpContext, JsonRpcResponse.FromError(JsonRpcErrorCodes.ExecutionError, request.Id, e));
                return;
            }
        }

        private static async Task WriteResponseAsync(HttpListenerContext context, JsonRpcResponse jsonRpcResponse)
        {
            context.Response.ContentType = "application/json";
            var jsonResponse = JsonConvert.SerializeObject(jsonRpcResponse, SerializerSettings);
            var byteResponse = Encoding.UTF8.GetBytes(jsonResponse);
            await context.Response.OutputStream.WriteAsync(byteResponse, 0, byteResponse.Length);
            context.Response.OutputStream.Close();
        }

        public static void Stop()
        {
            Listener?.Stop();
        }

        private static List<JsonRpcMethod> GetJsonRpcMethods()
        {
            var methods = new List<JsonRpcMethod>();
            foreach (var methodInfo in Methods.Values)
            {
                var method = new JsonRpcMethod();
                method.Name = methodInfo.Name;

                foreach (var parameterInfo in methodInfo.GetParameters())
                {
                    var parameterAttribute = parameterInfo.GetCustomAttribute<JsonRpcParameterAttribute>();
                    if (parameterAttribute?.Ignore ?? false) continue;

                    var parameter = new JsonRpcParameter();
                    parameter.Name = parameterAttribute?.Name ?? parameterInfo.Name;
                    parameter.Type = JsonTypeMap.GetJsonType(parameterInfo.ParameterType);
                    method.Parameters.Add(parameter);
                }

                methods.Add(method);
            }

            return methods;
        }
    }
}