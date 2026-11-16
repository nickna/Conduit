using Phase0;
try{
 if(args is ["--provider",var port]) await Provider.RunAsync(int.Parse(port));
 else throw new ArgumentException("Usage: --provider <loopback-port>");
}catch(Exception error){await Console.Error.WriteLineAsync(error.ToString());Environment.ExitCode=1;}
