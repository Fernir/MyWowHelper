// Based off code from https://github.com/JuJuBoSc/MyMemory

using System;
using System.Diagnostics;
using System.Linq;

namespace MyWowHelper
{
    internal class Program
    {
        private static void Main()
        {
            Console.Title = "My Wow Helper";

            var process = Process.GetProcessesByName("Wow").FirstOrDefault();
            if (process == null)
            {
                Console.WriteLine("Please launch wow");
                Console.ReadLine();
                return;
            }

            Console.WriteLine($"Connecting to WoW x86 with process id : {process.Id}");

            WoW.Connect(process);

            while (!WoW.IsIngame)
            {
                Console.WriteLine("Please login to game world.");
            }
            
            WoW.Print("BanBuddy Successfully Loaded... :)");
            
            Console.ReadLine();

            WoW.CastSpell("Flash of Light", "player");

            Console.WriteLine("Done :) Press any key to cleanup.");
            Console.ReadKey();
        }
    }
}