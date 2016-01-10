using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Configuration;
using System.Web.Mvc;
using Amazon.IotData;
using Amazon.IotData.Model;
using System.Text;
using System.IO;
using System.Web.Script.Serialization;

namespace WebUI.Controllers
{
    public class DefaultController : Controller
    {
        private static AmazonIotDataClient _client = new AmazonIotDataClient(ConfigurationManager.AppSettings["AccessKeyID"], 
                                             ConfigurationManager.AppSettings["SecretAccessKey"], ConfigurationManager.AppSettings["AwsIoTEndPoint"]);
        private const string UPDATE_JSON_TEMPLATE = "{{\"state\":{{\"desired\": {{\"code\":\"{0}\"}}}}}}";

        // GET: Default
        public ActionResult Index()
        {
            return View();
        }

        public ActionResult State(string devName)
        {
            GetThingShadowRequest req = new GetThingShadowRequest();
            req.ThingName = devName;

            GetThingShadowResponse res = _client.GetThingShadow(req);
            string state = Encoding.UTF8.GetString(res.Payload.ToArray());

            return Content(Request["callback"] + "(" + state + ")");
        }

        public ActionResult Select(string devName)
        {
            string jsonStr = string.Format(UPDATE_JSON_TEMPLATE, Request["emoji"]);
            UpdateThingShadowRequest req = new UpdateThingShadowRequest();
            req.ThingName = devName;
            req.Payload = new MemoryStream(Encoding.UTF8.GetBytes(jsonStr));
            UpdateThingShadowResponse res =  _client.UpdateThingShadow(req);
            string state = Encoding.UTF8.GetString(res.Payload.ToArray());

            return Content(Request["callback"] + "(" + state + ")");
        }
    }
}