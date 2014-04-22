﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WebApiThrottle
{
    public class ThrottlingHandler : DelegatingHandler
    {
        /// <summary>
        /// Creates a new instance of the <see cref="ThrottlingHandler"/> class.
        /// By default, the <see cref="QuotaExceededResponseCode"/> property 
        /// is set to 429 (Too Many Requests).
        /// </summary>
        public ThrottlingHandler()
        {
            QuotaExceededResponseCode = (HttpStatusCode)429;
            Repository = new CacheRepository();
        }

        /// <summary>
        /// Throttling rate limits policy
        /// </summary>
        public ThrottlePolicy Policy { get; set; }

        /// <summary>
        /// Throttle metrics storage
        /// </summary>
        public IThrottleRepository Repository { get; set; }

        /// <summary>
        /// Log traffic and blocked requests
        /// </summary>
        public IThrottleLogger Logger { get; set; }

        /// <summary>
        /// If none specifed the default will be: 
        /// API calls quota exceeded! maximum admitted {0} per {1}
        /// </summary>
        public string QuotaExceededMessage { get; set; }

        /// <summary>
        /// Gets or sets the value to return as the HTTP status 
        /// code when a request is rejected because of the
        /// throttling policy. The default value is 429 (Too Many Requests).
        /// </summary>
        public HttpStatusCode QuotaExceededResponseCode { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Policy.IpThrottling && !Policy.ClientThrottling && !Policy.EndpointThrottling)
                return base.SendAsync(request, cancellationToken);

            var identity = SetIndentity(request);
            if (IsWhitelisted(identity))
                return base.SendAsync(request, cancellationToken);

            TimeSpan timeSpan = TimeSpan.FromSeconds(1);

            var rates = Policy.Rates.AsEnumerable();
            if (Policy.StackBlockedRequests)
            {
                //all requests including the rejected ones will stack in this order: day, hour, min, sec
                //if a client hits the hour limit then the minutes and seconds counters will expire and will eventually get erased from cache
                rates = Policy.Rates.Reverse();
            }

            //apply policy
            //the IP rules are applied last and will overwrite any client rule you might defined
            foreach (var rate in rates)
            {
                var rateLimitPeriod = rate.Key;
                var rateLimit = rate.Value;

                switch (rateLimitPeriod)
                {
                    case RateLimitPeriod.Second:
                        timeSpan = TimeSpan.FromSeconds(1);
                        break;
                    case RateLimitPeriod.Minute:
                        timeSpan = TimeSpan.FromMinutes(1);
                        break;
                    case RateLimitPeriod.Hour:
                        timeSpan = TimeSpan.FromHours(1);
                        break;
                    case RateLimitPeriod.Day:
                        timeSpan = TimeSpan.FromDays(1);
                        break;
                    case RateLimitPeriod.Week:
                        timeSpan = TimeSpan.FromDays(7);
                        break;
                }

                //increment counter
                string requestId;
                var throttleCounter = ProcessRequest(identity, timeSpan, rateLimitPeriod, out requestId);

                if (throttleCounter.Timestamp + timeSpan < DateTime.UtcNow)
                    continue;

                //apply endpoint rate limits
                if (Policy.EndpointRules.Any())
                {
                    var rules = Policy.EndpointRules.Where(x => identity.Endpoint.Contains(x.Key.ToLowerInvariant())).ToList();
                    if (rules.Any())
                    {
                        //get the lower limit from all applying rules
                        var customRate = (from r in rules let rateValue = r.Value.GetLimit(rateLimitPeriod) select rateValue).Min();

                        if (customRate > 0)
                        {
                            rateLimit = customRate;
                        }
                    }
                }

                //apply custom rate limit for clients that will override endpoint limits
                if (Policy.ClientRules.Any() && Policy.ClientRules.Keys.Contains(identity.ClientKey))
                {
                    var limit = Policy.ClientRules[identity.ClientKey].GetLimit(rateLimitPeriod);
                    if (limit > 0) rateLimit = limit;
                }

                //enforce ip rate limit as is most specific 
                string ipRule = null;
                if (Policy.IpRules.Any() && ContainsIp(Policy.IpRules.Keys.ToList(), identity.ClientIp, out ipRule))
                {
                    var limit = Policy.IpRules[ipRule].GetLimit(rateLimitPeriod);
                    if (limit > 0) rateLimit = limit;
                }

                //check if limit is reached
                if (rateLimit > 0 && throttleCounter.TotalRequests > rateLimit)
                {
                    //log blocked request
                    if (Logger != null) Logger.Log(ComputeLogEntry(requestId, identity, throttleCounter, rateLimitPeriod.ToString(), rateLimit, request));
                   
                    //break execution
                    return QuotaExceededResponse(request,
                        GetQuotaExceededValue(rateLimit, rateLimitPeriod),
                        QuotaExceededResponseCode,
                        RetryAfterFrom(throttleCounter.Timestamp, rateLimitPeriod));
                }
            }

            //no throttling required
            return base.SendAsync(request, cancellationToken);
        }

        protected virtual object GetQuotaExceededValue(long rateLimit, RateLimitPeriod rateLimitPeriod)
        {
            // allows subclasses to return an object
            // My client expects the response in form of: { status: 403, message: "Per Minute Limit Exceeded" }

            string message;

            if (!string.IsNullOrEmpty(QuotaExceededMessage))
                message = QuotaExceededMessage;
            else
                message = "API calls quota exceeded! maximum admitted {0} per {1}.";

            return string.Format(message, rateLimit, rateLimitPeriod);
        }

        protected virtual RequestIdentity SetIndentity(HttpRequestMessage request)
        {
            var entry = new RequestIdentity();
            entry.ClientIp = GetClientIp(request).ToString();
            entry.Endpoint = request.RequestUri.AbsolutePath.ToLowerInvariant();
            entry.ClientKey = request.Headers.Contains("Authorization-Token") ? request.Headers.GetValues("Authorization-Token").First() : "anon";

            return entry;
        }

        static readonly object _processLocker = new object();
        private ThrottleCounter ProcessRequest(RequestIdentity requestIdentity, TimeSpan timeSpan, RateLimitPeriod period, out string id)
        {
            var throttleCounter = new ThrottleCounter()
                {
                    Timestamp = DateTime.UtcNow,
                    TotalRequests = 1
                };

            id = ComputeThrottleKey(requestIdentity, period);

            //serial reads and writes
            lock (_processLocker)
            {
                var entry = Repository.FirstOrDefault(id);
                if (entry.HasValue)
                {
                    //entry has not expired
                    if (entry.Value.Timestamp + timeSpan >= DateTime.UtcNow)
                    {
                        //increment request count
                        var totalRequests = entry.Value.TotalRequests + 1;

                        //deep copy
                        throttleCounter = new ThrottleCounter
                        {
                            Timestamp = entry.Value.Timestamp,
                            TotalRequests = totalRequests
                        };

                    }
                }

                //stores: id (string) - timestamp (datetime) - total (long)
                Repository.Save(id, throttleCounter, timeSpan);
            }

            return throttleCounter;
        }

        protected virtual string ComputeThrottleKey(RequestIdentity requestIdentity, RateLimitPeriod period)
        {
            var keyValues = new List<string>()
                {
                    "throttle"
                };

            if (Policy.IpThrottling)
                keyValues.Add(requestIdentity.ClientIp);

            if (Policy.ClientThrottling)
                keyValues.Add(requestIdentity.ClientKey);

            if (Policy.EndpointThrottling)
                keyValues.Add(requestIdentity.Endpoint);

            keyValues.Add(period.ToString());

            var id = string.Join("_", keyValues);
            var idBytes = Encoding.UTF8.GetBytes(id);
            var hashBytes = new System.Security.Cryptography.SHA1Managed().ComputeHash(idBytes);
            var hex = BitConverter.ToString(hashBytes).Replace("-", "");
            return hex;
        }

        private string RetryAfterFrom(DateTime timestamp, RateLimitPeriod period)
        {
            var secondsPast = Convert.ToInt32((DateTime.UtcNow - timestamp).TotalSeconds);
            var retryAfter = 1;
            switch (period)
            {
                case RateLimitPeriod.Minute:
                    retryAfter = 60;
                    break;
                case RateLimitPeriod.Hour:
                    retryAfter = 60 * 60;
                    break;
                case RateLimitPeriod.Day:
                    retryAfter = 60 * 60 * 24;
                    break;
                case RateLimitPeriod.Week:
                    retryAfter = 60 * 60 * 24 * 7;
                    break;
            }
            retryAfter = retryAfter > 1 ? retryAfter - secondsPast : 1;
            return retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        protected IPAddress GetClientIp(HttpRequestMessage request)
        {
            IPAddress ipAddress;

            if (request.Properties.ContainsKey("MS_HttpContext"))
            {
                var ok = IPAddress.TryParse(((HttpContextBase)request.Properties["MS_HttpContext"]).Request.UserHostAddress, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }

            if (request.Properties.ContainsKey(RemoteEndpointMessageProperty.Name))
            {
                var ok = IPAddress.TryParse(((RemoteEndpointMessageProperty)request.Properties[RemoteEndpointMessageProperty.Name]).Address, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }

            if (request.Properties.ContainsKey("MS_OwinContext"))
            {
                var ok = IPAddress.TryParse(((Microsoft.Owin.OwinContext)request.Properties["MS_OwinContext"]).Request.RemoteIpAddress, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }


            return null;
        }

        private bool IsWhitelisted(RequestIdentity requestIdentity)
        {
            if (Policy.IpThrottling)
                if (Policy.IpWhitelist.Any() && ContainsIp(Policy.IpWhitelist, requestIdentity.ClientIp))
                    return true;

            if (Policy.ClientThrottling)
                if (/*Policy.ClientWhitelist.Any() &&*/ Policy.ClientWhitelist.Contains(requestIdentity.ClientKey))
                    return true;

            if (Policy.EndpointThrottling)
                if (/*Policy.EndpointWhitelist.Any() &&*/ Policy.EndpointWhitelist.Any(x => requestIdentity.Endpoint.Contains(x.ToLowerInvariant())))
                    return true;

            return false;
        }

        private bool ContainsIp(List<string> ipRules, string clientIp)
        {
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var rule in ipRules)
                {
                    var range = new IPAddressRange(rule);
                    if (range.Contains(ip)) return true;
                }
            }

            return false;
        }

        private bool ContainsIp(List<string> ipRules, string clientIp, out string rule)
        {
            rule = null;
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var r in ipRules)
                {
                    var range = new IPAddressRange(r);
                    if (range.Contains(ip))
                    {
                        rule = r;
                        return true;
                    }
                }
            }

            return false;
        }

        private Task<HttpResponseMessage> QuotaExceededResponse(HttpRequestMessage request, object message, HttpStatusCode responseCode, string retryAfter)
        {
            var response = request.CreateResponse(responseCode, message);
            response.Headers.Add("Retry-After", new string[] { retryAfter });
            return Task.FromResult(response);
        }

        private ThrottleLogEntry ComputeLogEntry(string requestId, RequestIdentity identity, ThrottleCounter throttleCounter, string rateLimitPeriod, long rateLimit, HttpRequestMessage request)
        {
            return new ThrottleLogEntry
                    {
                        ClientIp = identity.ClientIp,
                        ClientKey = identity.ClientKey,
                        Endpoint = identity.Endpoint,
                        LogDate = DateTime.UtcNow,
                        RateLimit = rateLimit,
                        RateLimitPeriod = rateLimitPeriod,
                        RequestId = requestId,
                        StartPeriod = throttleCounter.Timestamp,
                        TotalRequests = throttleCounter.TotalRequests,
                        Request = request
                    };
        }
    }
}
