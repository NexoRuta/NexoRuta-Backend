using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NexoRuta.Infrastructure.Webhooks;
namespace NexoRuta.UnitTests.Webhooks;
public sealed class WebhookSecurityTests
{
    [Fact] public void StandardSignatureMatchesIndependentHmacOverExactBytes()
    {
        var key=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();var secret="whsec_"+Convert.ToBase64String(key);
        const string id="019a0000-0000-7000-8000-000000000001",stamp="1794100000";
        var body=Encoding.UTF8.GetBytes("{\"estado\":\"Entregado\", \"nota\":\"ñ\"}");
        var signed=Encoding.UTF8.GetBytes(id+"."+stamp+".").Concat(body).ToArray();
        Assert.Equal("v1,"+Convert.ToBase64String(HMACSHA256.HashData(key,signed)),WebhookSignature.Sign(secret,id,stamp,body));
        Assert.NotEqual(WebhookSignature.Sign(secret,id,stamp,body),WebhookSignature.Sign(secret,id,stamp,body.Concat(new byte[]{32}).ToArray()));
    }
    [Theory]
    [InlineData("127.0.0.1",false)][InlineData("::1",false)][InlineData("::ffff:10.2.3.4",false)]
    [InlineData("169.254.169.254",false)][InlineData("100.100.1.2",false)][InlineData("172.20.1.2",false)]
    [InlineData("192.168.1.2",false)][InlineData("224.0.0.1",false)][InlineData("2001:db8::1",false)]
    [InlineData("8.8.8.8",true)][InlineData("2606:4700:4700::1111",true)]
    public void RejectsPrivateAndReservedAddresses(string ip,bool allowed)=>Assert.Equal(allowed,WebhookDestinationPolicy.IsPublic(IPAddress.Parse(ip)));
    [Fact] public void DemoExceptionIsExactAndUnavailableInProduction()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Webhooks:DevelopmentReceiverUrl","http://127.0.0.1:5103/comercio/webhook"},{"Webhooks:AllowedOrigins:0","https://merchant.example.test"}}).Build();
        var policy=new WebhookDestinationPolicy(config,new TestEnvironment());
        policy.Validate("http://127.0.0.1:5103/comercio/webhook");
        policy.Validate("https://merchant.example.test/webhook");
        Assert.Throws<ArgumentException>(()=>policy.Validate("http://127.0.0.1:5103/other"));
        Assert.Throws<ArgumentException>(()=>policy.Validate("https://merchant.example.test@evil.example/"));
        Assert.Throws<ArgumentException>(()=>policy.Validate("https://merchant.example.test/webhook?token=secret"));
        Assert.Throws<ArgumentException>(()=>new WebhookDestinationPolicy(config,new TestEnvironment{EnvironmentName="Production"}).Validate("http://127.0.0.1:5103/comercio/webhook"));
    }
    private sealed class TestEnvironment:IHostEnvironment
    {public string EnvironmentName{get;set;}="Development";public string ApplicationName{get;set;}="Tests";public string ContentRootPath{get;set;}="/tmp";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
}
