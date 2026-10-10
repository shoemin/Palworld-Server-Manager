using PalworldServerManager.Core.Settings;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

internal static class PreservedSettingDocumentTests
{
    private const string Header="[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=";
    private static void Check(bool value){if(!value)throw new Exception("Preserved settings assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();throw new Exception("Expected settings refusal.");}catch(T){}}
    public static Task ExactRoundTripAndOneValuePatch()
    {
        var text="; leading comment\r\n"+Header+"( \tExpRate = 1.000 , FutureBlob = (Name=\"a,b\\\"c\",List=[One,{X=Y}]) ,AdminPassword=\"old\")\r\n; end\r\n";
        var original=PreservedPalworldConfigDocument.Parse(text);
        Check(original.Serialize()==text&&ReferenceEquals(original,original.WithValues(new Dictionary<string,string>())));
        var changed=original.WithValues(new Dictionary<string,string>{{"EXPRATE","2.5"}});
        Check(changed.Serialize()==text.Replace("1.000","2.5",StringComparison.Ordinal)&&original.Serialize()==text);
        Check(changed.Get("FutureBlob")==PalworldConfigParser.Parse(text).Get("FutureBlob")&&changed.Get("AdminPassword")=="\"old\"");
        return Task.CompletedTask;
    }
    public static Task AdditionsAndUnicodeAreExplicit()
    {
        var original=PreservedPalworldConfigDocument.Parse(Header+"(  )\r\n; retained");
        var raw=PalworldConfigParser.Quote("世界, \"quoted\" (room) C:\\server");
        var changed=original.WithValues(new Dictionary<string,string>{{"ServerName",raw},{"FutureThing","(One,Two)"}});
        Check(changed.Serialize()==Header+"(  ServerName="+raw+",FutureThing=(One,Two))\r\n; retained");
        Check(changed.Get("ServerName")==raw&&PalworldConfigParser.Unquote(changed.Get("ServerName"))=="世界, \"quoted\" (room) C:\\server");
        var added=changed.WithValues(new Dictionary<string,string>{{"ExpRate","1.000"}});
        Check(added.Serialize()==changed.Serialize().Replace("))\r\n; retained", "),ExpRate=1.000)\r\n; retained",StringComparison.Ordinal));
        return Task.CompletedTask;
    }
    public static Task DuplicatePreservationAndAmbiguousEditRefusal()
    {
        var text=Header+"(Future=One,Future=Two,ExpRate=1)\n";var doc=PreservedPalworldConfigDocument.Parse(text);
        Check(doc.Serialize()==text&&doc.Get("Future")=="Two");
        Check(doc.WithValues(new Dictionary<string,string>{{"ExpRate","2"}}).Serialize()==text.Replace("ExpRate=1","ExpRate=2",StringComparison.Ordinal));
        Reject<InvalidOperationException>(()=>doc.WithValues(new Dictionary<string,string>{{"Future","Three"}}));
        Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>(StringComparer.Ordinal){{"ExpRate","2"},{"EXPRATE","3"}}));
        Check(doc.Serialize()==text);return Task.CompletedTask;
    }
    public static Task EmptyFinalValueAndAdditionDoNotCollide()
    {
        foreach(var text in new[]{Header+"(A=)",Header+"(Before=One,A=   )",Header+"(A=) ; retained\r\n"})
        foreach(var changes in new[]{new Dictionary<string,string>{{"A","2"},{"B","3"}},new Dictionary<string,string>{{"B","3"},{"A","2"}}})
        {
            var original=PreservedPalworldConfigDocument.Parse(text);var changed=original.WithValues(changes);
            Check(changed.Get("A")=="2"&&changed.Get("B")=="3"&&original.Serialize()==text);
            Check(changed.Serialize()==text.Replace(")","2,B=3)",StringComparison.Ordinal));
        }
        return Task.CompletedTask;
    }
    public static Task InjectedValuesCannotEscapeTheirEntry()
    {
        var text=Header+"(ExpRate=1,AdminPassword=\"preserved\")";var doc=PreservedPalworldConfigDocument.Parse(text);
        foreach(var value in new[]{"2,AdminPassword=\"changed\"","2) ; trailing","2)\nOptionSettings=(AdminPassword=\"changed\"","([)]","\"unterminated","2;comment","2#comment",""," 2","2\0"})
            Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>{{"ExpRate",value}}));
        foreach(var key in new[]{"", "ExpRate,AdminPassword", "ExpRate=2", "[section]", "ExpRate\n"})
            Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>{{key,"2"}}));
        Check(doc.Serialize()==text&&doc.Get("AdminPassword")=="\"preserved\"");return Task.CompletedTask;
    }
    public static Task UnsupportedSourceRefusesWithoutNormalization()
    {
        foreach(var text in new[]{"OptionSettings=(A=1)","[Other]\nOptionSettings=(A=1)",Header+"(A=1)\nOptionSettings=(B=2)",
            "; OptionSettings=(Fake=1)\n"+Header+"(A=1)",Header+"(A=([)]))",Header+"(A=\"unterminated)",Header+"(A=1,,B=2)",Header+"(A=1,)" ,Header+"(A=1)extra",Header+"(NoEquals)",Header+"(A=1\0)"})
            Reject<FormatException>(()=>PreservedPalworldConfigDocument.Parse(text));
        var future=Header+"(Unknown.Empty=,未来設定=(Value=Future),ExpRate=1)\r\n";
        Check(PreservedPalworldConfigDocument.Parse(future).WithValues(new Dictionary<string,string>{{"ExpRate","2"}}).Serialize()==future.Replace("ExpRate=1","ExpRate=2",StringComparison.Ordinal));
        return Task.CompletedTask;
    }
    public static Task BoundsAndSnapshots()
    {
        Reject<FormatException>(()=>PreservedPalworldConfigDocument.Parse(new string('x',PreservedPalworldConfigDocument.MaximumCharacters+1)));
        Reject<FormatException>(()=>PreservedPalworldConfigDocument.Parse(Header+"(A="+new string('(',65)+"x"+new string(')',65)+")"));
        var doc=PreservedPalworldConfigDocument.Parse(Header+"(A=1)");
        Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>{{"A",new string('x',PreservedPalworldConfigDocument.MaximumCharacters+1)}}));
        var large=new string('x',PreservedPalworldConfigDocument.MaximumCharacters-100);
        Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>{{"A",large},{"B",large},{"C",large}}));
        Reject<FormatException>(()=>doc.WithValues(new Dictionary<string,string>{{"A",large},{"B",large}}));
        try{((IList<string>)doc.Keys)[0]="changed";throw new Exception("Mutable keys.");}catch(NotSupportedException){}
        Check(doc.Keys.SequenceEqual(new[]{"A"})&&doc.ToString()!.Contains(nameof(PreservedPalworldConfigDocument))&&!doc.ToString()!.Contains("A=1",StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public static Task SeededNestedPreservation()
    {
        var random=new Random(47002);
        string Value(int depth)
        {
            if(depth==0||random.Next(3)==0)return random.Next(2)==0?"Token"+random.Next(100):PalworldConfigParser.Quote("a, b=()[]{}\\\"世界"+random.Next(100));
            var bracket=random.Next(3);var left=bracket==0?"(":bracket==1?"[":"{";var right=bracket==0?")":bracket==1?"]":"}";
            return left+Value(depth-1)+","+Value(depth-1)+right;
        }
        for(var i=0;i<500;i++)
        {
            var unknown=Value(4);var raw="; comment\r\n"+Header+"(ExpRate = 1.000 , Future = "+unknown+" ,Other=\"unchanged\")\r\n; tail";
            var doc=PreservedPalworldConfigDocument.Parse(raw);
            var patched=doc.WithValues(new Dictionary<string,string>{{"ExpRate","2.5"}});
            Check(doc.Serialize()==raw&&patched.Serialize()==raw.Replace("ExpRate = 1.000","ExpRate = 2.5",StringComparison.Ordinal)&&patched.Get("Future")==unknown);
        }
        var many=PreservedPalworldConfigDocument.Parse(Header+"("+string.Join(',',Enumerable.Range(0,10000).Select(i=>$"Future{i}=Old"))+")");
        var edits=Enumerable.Range(0,10000).ToDictionary(i=>$"Future{i}",_=>"New");
        var changed=many.WithValues(edits);
        Check(changed.Serialize()==many.Serialize().Replace("=Old","=New",StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
