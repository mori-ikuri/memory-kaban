using System.Text;
using Bag;

Console.OutputEncoding = new UTF8Encoding(false);
return BagCommand.Run(args, Console.Out, Console.Error);
