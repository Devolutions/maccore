//
// This program takes the API definition from the build and
// uses it to generate the documentation for the auto-generated
// code.   
//
// Unlike the other tools, the documentation generated by this tool
// is based on the knowledge from the API contract file that is
// erased in the compilation process (like the link between events
// and their ObjC delegate classes).
//

using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Xml.XPath;
using System.Xml;
using System.Text;

#if MONOMAC
using MonoMac.Foundation;
#else
using MonoTouch.Foundation;
#endif
using macdoc;

class DocumentGeneratedCode {
#if MONOMAC
	Type nso = typeof (MonoMac.Foundation.NSObject);
	const string ns = "MonoMac";
	const string docBase = "/Developer/Documentation/DocSets/com.apple.adc.documentation.AppleSnowLeopard.CoreReference.docset";
#else
	Type nso = typeof (MonoTouch.Foundation.NSObject);
	const string ns = "MonoTouch";
	const string docBase = "/Library/Developer/Shared/Documentation/DocSets/com.apple.adc.documentation.AppleiOS5_0.iOSLibrary.docset";
#endif

	static void Help ()
	{
		Console.WriteLine ("Usage is: document-generated-code [--appledocs] temp.dll path-to-documentation");
	}

	static string assembly_dir;
	static Assembly assembly;
	static bool mergeAppledocs;
	static AppleDocMerger docGenerator;

	static Dictionary<Type,XDocument> docs = new Dictionary<Type,XDocument> ();
	
	static string GetMdocPath (Type t, bool notification = false)
	{
		var ns = t.Namespace;
		var typeName = t.FullName.Substring (ns.Length+1);
		return String.Format ("{0}/{1}/{2}{3}.xml", assembly_dir, ns, typeName, notification ? "+Notifications" : "");
	}
	
	static XDocument GetDoc (Type t, bool notification = false)
	{
		if (notification == false && docs.ContainsKey (t))
			return docs [t];
		
		string xmldocpath = GetMdocPath (t, notification);
		
		if (!File.Exists (xmldocpath)) {
			Console.WriteLine ("Document missing for type: {0} (File missing={1}), must run update-docs", t.FullName, xmldocpath);
			return null;
		}
		
		XDocument xmldoc;
		try {
			using (var f = File.OpenText (xmldocpath))
				xmldoc = XDocument.Load (f);
			if (notification == false)
				docs [t] = xmldoc;
		} catch {
			Console.WriteLine ("Failure while loading {0}", xmldocpath);
			return null;
		}

		return xmldoc;
	}

	static void Save (string xmldocpath, XDocument xmldoc)
	{
		var xmlSettings = new XmlWriterSettings (){
			Indent = true,
			Encoding = new UTF8Encoding (false),
			OmitXmlDeclaration = true,
			NewLineChars = Environment.NewLine
		};
		using (var output = File.CreateText (xmldocpath)){
			var xmlw = XmlWriter.Create (output, xmlSettings);
			xmldoc.Save (xmlw);
			output.WriteLine ();
		}
	}
	
	static void SaveDocs ()
	{
		foreach (var t in docs.Keys){
			var xmldocpath = GetMdocPath (t);
			var xmldoc = docs [t];

			Save (xmldocpath, xmldoc);
		}
	}

	//
	// Handles fields, but perhaps this is better done in DocFixer to pull the definitions
	// from the docs?
	//
	public static void ProcessField (Type t, XDocument xdoc, PropertyInfo pi)
	{
		var fieldAttr = pi.GetCustomAttributes (typeof (FieldAttribute), true);
		if (fieldAttr.Length == 0)
			return;
		
		var export = ((FieldAttribute) fieldAttr [0]).SymbolName;
		
		var field = xdoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + pi.Name + "']");
		if (field == null){
			if (!warnings_up_to_date.ContainsKey (t)){
				Console.WriteLine ("Warning: {0} document is not up-to-date with the latest assembly (could not find Field <Member MemberName='{1}')", t, pi.Name);
				warnings_up_to_date [t] = true;
			}
			return;
		}
		var returnType = field.XPathSelectElement ("ReturnValue/ReturnType");
		var summary = field.XPathSelectElement ("Docs/summary");
		var remarks = field.XPathSelectElement ("Docs/remarks");
		var example = field.XPathSelectElement ("Docs/remarks/example");
		if (mergeAppledocs){
			if (returnType.Value == "MonoMac.Foundation.NSString" && export.EndsWith ("Notification")){
				var mdoc = docGenerator.GetAppleMemberDocs (ToCecilType (t), export);
				if (mdoc == null){
					Console.WriteLine ("Failed to load docs for {0} - {1}", t.Name, export);
					return;
				}

				var section = docGenerator.ExtractSection (mdoc);

				//
				// Make this pretty, the first paragraph we turn into the summary,
				// the rest we put in the remarks section
				//
				summary.Value = "";
				summary.Add (section);

				var skipOne = summary.Nodes ().Skip (2).ToArray ();
				remarks.Value = "";
				remarks.Add (skipOne);
				foreach (var n in skipOne)
					n.Remove ();
				if (example != null)
					remarks.Add (example);
			}
		}
	}

	//
	// Handles notifications
	//
	static Dictionary<Type,List<Type>> event_args_to_notification_uses = new Dictionary<Type,List<Type>> ();
	static Dictionary<Type,bool> warnings_up_to_date = new Dictionary<Type, bool> ();
	static List<Type> nested_types = new List<Type> ();
	
	public static void ProcessNotification (Type t, XDocument xdoc, PropertyInfo pi)
	{
		var notification = pi.GetCustomAttributes (typeof (NotificationAttribute), true);
		if (notification.Length == 0)
			return;
		
		var notification_event_args = ((NotificationAttribute) notification [0]).Type;
		
		var field = xdoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + pi.Name + "']");
		if (field == null){
			if (!warnings_up_to_date.ContainsKey (t)){
				Console.WriteLine ("WARNING: {0} document is not up-to-date with the latest assembly", t);
				warnings_up_to_date [t] = true;
			}
			return;
		}
		var name = pi.Name;
		var mname = name.Substring (0, name.Length-("Notification".Length));
		
		var returnType = field.XPathSelectElement ("ReturnValue/ReturnType");
		var summary = field.XPathSelectElement ("Docs/summary");
		var remarks = field.XPathSelectElement ("Docs/remarks");
		var example = field.XPathSelectElement ("Docs/remarks/example");

		var body = new StringBuilder ("    Console.WriteLine (\"Notification: {0}\", args.Notification);");
	
		if (notification_event_args != null){
			body.Append ("\n");
			foreach (var p in notification_event_args.GetProperties ()){
				body.AppendFormat ("\n    Console.WriteLine (\"{0}\", args.{0});", p.Name);
			}
		}

		if (remarks.Value == "To be added.")
			remarks.Value = "";
		
		remarks.AddFirst (XElement.Parse (String.Format ("<para id='tool-remark'>If you want to subscribe to this notification, you can use the convenience <see cref='T:{0}+Notifications'/>.<see cref='M:{0}+Notifications.Observe{1}'/> method which offers strongly typed access to the parameters of the notification.</para>", t.Name, mname)));
		remarks.Add (XElement.Parse ("<para>The following example shows how to use the strongly typed Notifications class, to take the guesswork out of the available properties in the notification:</para>"));
		remarks.Add (XElement.Parse (String.Format ("<example><code lang=\"c#\">\n" +
							    "//\n// Lambda style\n//\n\n// listening\n" +
							    "notification = {0}.Notifications.Observe{1} ((sender, args) => {{\n    /* Access strongly typed args */\n{2}\n}});\n\n" +
							    "// To stop listening:\n" +
							    "notification.Dispose ();\n\n" +
							    "//\n// Method style\n//\nNSObject notification;\n" +
							    "void Callback (object sender, {1} args)\n"+
							    "{{\n    // Access strongly typed args\n{2}\n}}\n\n" +
							    "void Setup ()\n{{\n" +
							    "    notification = {0}.Notifications.Observe{1} (Callback);\n}}\n\n" +
							    "void Teardown ()\n{{\n" +
							    "    notification.Dispose ();\n}}</code></example>", t.Name, mname, body)));

		// Keep track of the uses, so we can list all of the observers.
		if (notification_event_args != null){
			List<Type> list;
			if (!event_args_to_notification_uses.ContainsKey (notification_event_args))
				list = new List<Type> ();
			else
				list = event_args_to_notification_uses [notification_event_args];
			list.Add (notification_event_args);
			event_args_to_notification_uses [notification_event_args] = list;
		}
		DocumentNotificationNestedType (t, pi, body.ToString ());
	}

	public static void DocumentNotificationNestedType (Type t, PropertyInfo pi, string body)
	{
		var class_doc = GetDoc (t, true);

		if (class_doc == null){
			Console.WriteLine ("Error, can not find Notification class for type {0}", t);
			return;
		}

		var class_summary = class_doc.XPathSelectElement ("Type/Docs/summary");
		var class_remarks = class_doc.XPathSelectElement ("Type/Docs/remarks");

		class_summary.Value = "Notification posted by the <see cref =\"T:" + t.FullName + "\"/> class.";
		class_remarks.Value = "";
		class_remarks.Add (XElement.Parse ("<para>This is a static class which contains various helper methods that allow developers to observe events posted " +
						   "in the iOS notification hub (<see cref=\"T:MonoTouch.Foundation.NSNotificationCenter\"/>).</para>"));
		class_remarks.Add (XElement.Parse ("<para>The methods defined in this class post events invoke the provided method or lambda with a " +
						   "<see cref=\"T:MonoTouch.Foundation.NSNotificationEventArgs\"/> parameter which contains strongly typed properties for the notification arguments.</para>"));

		var notifications = from prop in t.GetProperties ()
			let propName = prop.Name
			where propName == pi.Name
			let fieldAttrs = prop.GetCustomAttributes (typeof (FieldAttribute), true)
			where fieldAttrs.Length > 0 && prop.GetCustomAttributes (typeof (NotificationAttribute), true).Length > 0
			let propLen = propName.Length
			let convertedName = propName.EndsWith ("Notification") ? propName.Substring (0, propLen-("Notification".Length)) : propName
			select new Tuple<string,string> (convertedName, ((FieldAttribute) fieldAttrs [0]).SymbolName) ;

		foreach (var notification in notifications){
			var mname = "Observe" + notification.Item1;
			var method = class_doc.XPathSelectElement ("Type/Members/Member[@MemberName='" + mname + "']");

			var handler = method.XPathSelectElement ("Docs/param");
			var summary = method.XPathSelectElement ("Docs/summary");
			var remarks = method.XPathSelectElement ("Docs/remarks");
			var returns = method.XPathSelectElement ("Docs/returns");
			if (handler == null)
				Console.WriteLine ("Looking for {0}, and this is the class\n{1}", notification.Item1, class_doc);
			handler.Value = "Method to invoke when the notification is posted.";
			summary.Value = "Registers a method to be notified when the " + notification.Item2 + " notification is posted.";
			returns.Value = "The returned NSObject represents the registered notification.   Either call Dispose on the object to stop receiving notifications, or pass it to <see cref=\"M:MonoTouch.Foundation.NSNotification.RemoveObserver\"/>";
			remarks.Value = "";
			remarks.Add (XElement.Parse ("<para>The following example shows how you can use this method in your code</para>"));

			remarks.Add (XElement.Parse (String.Format ("<example><code lang=\"c#\">\n" +
								    "//\n// Lambda style\n//\n\n// listening\n" +
								    "notification = {0}.Notifications.Observe{1} ((sender, args) => {{\n    /* Access strongly typed args */\n{2}\n}});\n\n" +
								    "// To stop listening:\n" +
								    "notification.Dispose ();\n\n" +
								    "//\n//Method style\n//\nNSObject notification;\n" +
								    "void Callback (object sender, {1} args)\n"+
								    "{{\n    // Access strongly typed args\n{2}\n}}\n\n" +
								    "void Setup ()\n{{\n" +
								    "    notification = {0}.Notifications.Observe{1} (Callback);\n}}\n\n" +
								    "void Teardown ()\n{{\n" +
								    "    notification.Dispose ();\n}}</code></example>", t.Name, mname, body)));
		
		}
		Save (GetMdocPath (t, true), class_doc);
		
	}

	public static void PopulateEvents (XDocument xmldoc, BaseTypeAttribute bta, Type t)
	{
		for (int i = 0; i < bta.Events.Length; i++){
			var delType = bta.Events [i];
			var evtName = bta.Delegates [i];
			foreach (var mi in delType.GatherMethods ()){
				var method = xmldoc.XPathSelectElement ("Type/Members/Member[@MemberName='" + mi.Name + "']");
				if (method == null){
					Console.WriteLine ("Documentation not up to date for {0}, member {1} was not found", delType, mi.Name);
					continue;
				}
				var summary = method.XPathSelectElement ("Docs/summary");
				var remarks = method.XPathSelectElement ("Docs/remarks");
				var returnType = method.XPathSelectElement ("ReturnValue/ReturnType");

				if (mi.ReturnType == typeof (void)){
					summary.Value = "Event raised by the object.";
					remarks.Value = "If you assign a value to this event, this will reset the value for the " + evtName + " property to an internal handler that maps delegates to events.";
				} else {
					summary.Value = "Delegate invoked by the object to get a value.";
					remarks.Value = "You assign a function, delegate or anonymous method to this property to return a value to the object.   If you assign a value to this property, it this will reset the value for the " + evtName + " property to an internal handler that maps delegates to events.";
				}
			}
		}
	}
	
	public static void ProcessNSO (Type t, BaseTypeAttribute bta)
	{
		var xmldoc = GetDoc (t);
		if (xmldoc == null)
			return;
		
		foreach (var pi in t.GatherProperties ()){
			object [] attrs;
			var kbd = false;
			if (pi.GetCustomAttributes (typeof (FieldAttribute), true).Length > 0){
				ProcessField (t, xmldoc, pi);

				if ((attrs = pi.GetCustomAttributes (typeof (NotificationAttribute), true)).Length > 0)
					ProcessNotification (t, xmldoc, pi);
				continue;
			}
			
		}

		if (bta != null && bta.Events != null){
			PopulateEvents (xmldoc, bta, t);
		}
	}
			
	public static int Main (string [] args)
	{
		string dir = null;
		string lib = null;
		var debug = Environment.GetEnvironmentVariable ("DOCFIXER");
		bool debugDoc = false;
		
		for (int i = 0; i < args.Length; i++){
			var arg = args [i];
			if (arg == "-h" || arg == "--help"){
				Help ();
				return 0;
			}
			if (arg == "--appledocs"){
				mergeAppledocs = true;
				continue;
			}
			if (arg == "--debugdoc"){
				debugDoc = true;
				continue;
			}
			
			if (lib == null)
				lib = arg;
			else
				dir = arg;
		}
		
		if (dir == null){
			Help ();
			return 1;
		}
		
		if (File.Exists (Path.Combine (dir, "en"))){
			Console.WriteLine ("The directory does not seem to be the root for documentation (missing `en' directory)");
			return 1;
		}
		assembly_dir = Path.Combine (dir, "en");
		assembly = Assembly.LoadFrom (lib);

		if (mergeAppledocs){
			docGenerator = new AppleDocMerger (new AppleDocMerger.Options {
				DocBase = Path.Combine (docBase, "Contents/Resources/Documents/documentation"),
				DebugDocs = debugDoc,
				MonodocArchive = new MDocDirectoryArchive (assembly_dir),
					Assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly (lib),
					BaseAssemblyNamespace = ns,
					ImportSamples = false
					});
		}

		foreach (Type t in assembly.GetTypes ()){
			if (debugDoc && mergeAppledocs){
				string str = docGenerator.GetAppleDocFor (ToCecilType (t));
				if (str == null){
					Console.WriteLine ("Could not find docs for {0}", t);
				}
				
				continue;
			}
			
			if (debug != null && t.FullName != debug)
				continue;

			var btas = t.GetCustomAttributes (typeof (BaseTypeAttribute), true);
			ProcessNSO (t, btas.Length > 0  ? (BaseTypeAttribute) btas [0] : null);
		}

		foreach (Type notification_event_args in event_args_to_notification_uses.Keys){
			var uses = event_args_to_notification_uses [notification_event_args];

			
		}
		Console.WriteLine ("saving");
		SaveDocs ();
		
		return 0;
	}
	
	static Mono.Cecil.TypeDefinition ToCecilType (Type t)
	{
		return new Mono.Cecil.TypeDefinition (t.Namespace, t.Name, (Mono.Cecil.TypeAttributes)t.Attributes);
	}
}