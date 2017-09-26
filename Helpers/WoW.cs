// Copyright (C) 2017 Julian Bosch
// See the file LICENSE for copying permission.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Helpers;
using MyMemory;
using MyMemory.Memory;

namespace MyWowHelper
{
    /// <summary>
    ///     Class that allow calling code by the message handler thread in a remote process
    /// </summary>
    public static class WoW
    {
        private const int GWL_WNDPROC = -4;

        private static readonly object locker = new object();

        private static RemoteProcess m_Process;
        private static IntPtr m_WindowHandle;
        private static Random m_Random;
        private static int m_CustomMessageCode;
        private static RemoteAllocatedMemory m_Data;
        private static IntPtr m_WndProcFunction;
        private static IntPtr m_OriginalWndProc;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr p_Hwnd, uint p_Msg, IntPtr p_WParam, IntPtr p_LParam);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr p_Module, string p_ProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string p_ModuleName);

        public static void Connect(Process p)
        {
            m_WindowHandle = p.MainWindowHandle;
            m_Process = new RemoteProcess((uint) p.Id);
            m_Random = new Random();
            m_CustomMessageCode = m_Random.Next(0x8000, 0xBFFF); // From MSDN : WM_APP (0x8000) through 0xBFFF
            m_Data = m_Process.MemoryManager.AllocateMemory(0x1000);
            m_WndProcFunction = m_Data.AllocateOfChunk("WndProc", 0x500);
            m_OriginalWndProc = m_Data.AllocateOfChunk<IntPtr>("OriginalWndProc");

            var l_User32 = GetModuleHandle("user32.dll");
            var l_CallWindowProcW = GetProcAddress(l_User32, "CallWindowProcW");
            var l_SetWindowLongW = GetProcAddress(l_User32, "SetWindowLongW");

            // Setup our WndProc callback
            var l_Mnemonics = new[]
            {
                "mov eax, [esp+0x8]", // Get the message code from the stack
                $"cmp eax, {m_CustomMessageCode}", // Check if the message code is our custom one
                "jne @call_original", // Otherwise simply call the original WndProc

                "mov eax, [esp+0xC]", // Function pointer
                "mov edx, [esp+0x10]", // Result pointer
                "push edx", // Save result pointer
                "call eax", // Call the user function
                "pop edx", // Restore the result pointer
                "mov [edx], eax", // Save user function result
                "xor eax, eax", // We handled the message
                "retn",

                "@call_original:",
                "mov ecx, [esp+0x4]", // Hwnd
                "mov edx, [esp+0x8]", // Msg
                "mov esi, [esp+0xC]", // WParam
                "mov edi, [esp+0x10]", // LParam
                $"mov eax, [{m_OriginalWndProc}]",
                "push edi", // LParam
                "push esi", // WParam
                "push edx", // Msg
                "push ecx", // Hwnd
                "push eax", // WndProc original
                $"call {l_CallWindowProcW}", // Call the original WndProc
                "retn 0x14"
            };
            m_Process.Yasm.Inject(l_Mnemonics, m_WndProcFunction);

            // Register our WndProc callback
            l_Mnemonics = new[]
            {
                $"push {m_WndProcFunction}",
                $"push {GWL_WNDPROC}",
                $"push {m_WindowHandle}",
                $"call {l_SetWindowLongW}",
                $"mov [{m_OriginalWndProc}], eax",
                "retn"
            };
            m_Process.Yasm.InjectAndExecute(l_Mnemonics);
        }

        public static void Dispose()
        {
            var l_User32 = GetModuleHandle("user32.dll");
            var l_SetWindowLongW = GetProcAddress(l_User32, "SetWindowLongW");

            // Restore the original WndProc callback
            var l_Mnemonics = new[]
            {
                $"mov eax, [{m_OriginalWndProc}]",
                "push eax",
                $"push {GWL_WNDPROC}",
                $"push {m_WindowHandle}",
                $"call {l_SetWindowLongW}",
                "retn"
            };
            m_Process.Yasm.InjectAndExecute(l_Mnemonics);

            m_Data?.Dispose();
        }

        private static IntPtr Call(string[] p_Mnemonics, uint p_BufferSize = 0x1000)
        {
            using (var l_Buffer = m_Process.MemoryManager.AllocateMemory(p_BufferSize))
            {
                m_Process.Yasm.Inject(p_Mnemonics, l_Buffer.Pointer);

                return Call(l_Buffer.Pointer);
            }
        }

        private static IntPtr Call(IntPtr p_Function)
        {
            using (var l_ResultBuffer = m_Process.MemoryManager.AllocateMemory((uint) IntPtr.Size))
            {
                SendMessage(m_WindowHandle, (uint) m_CustomMessageCode, p_Function, l_ResultBuffer.Pointer);

                return l_ResultBuffer.Read<IntPtr>();
            }
        }

        public static void Print(string message)
        {
            DoString($"print('{message}')");
        }

        public static bool IsIngame => m_Process.MemoryManager.Read<byte>(m_Process.ModulesManager.MainModule.BaseAddress + (int) Offsets.GameState) == 1;


        public static void DoString(string p_Lua)
        {
            lock (locker)
            {
                var l_FrameScript__ExecuteBuffer = m_Process.ModulesManager.MainModule.BaseAddress + (int)Offsets.Framescript_ExecuteBuffer;
                var l_LuaBufferUTF8 = Encoding.UTF8.GetBytes(p_Lua);

                using (var l_RemoteBuffer = m_Process.MemoryManager.AllocateMemory((uint) l_LuaBufferUTF8.Length + 1))
                {
                    l_RemoteBuffer.WriteBytes(l_LuaBufferUTF8);

                    var l_Mnemonics = new[]
                    {
                        "push 0",
                        $"push {l_RemoteBuffer.Pointer}",
                        $"push {l_RemoteBuffer.Pointer}",
                        $"call {l_FrameScript__ExecuteBuffer}",
                        "add esp, 0xC",
                        "retn"
                    };

                    Call(l_Mnemonics);
                }
            }
        }
        

        public static void CastSpell(string spell, string target = null)
        {
            if (target == null)
            {
                DoString($"CastSpellByName('{spell}')");
            }
            else
            {
                DoString($"TargetUnit('{target}')");
                Thread.Sleep(50);
                DoString($"CastSpellByName('{spell}')");
            }
        }

    }
}