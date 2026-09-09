using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using SharpDX.XInput;

class Program
{
    static SerialPort puertoSerial;
    static string nombrePuerto = "COM10";
    static int baudios = 9600;

    static void Main()
    {
        var controller = new Controller(UserIndex.One);

        if (!controller.IsConnected)
        {
            Console.WriteLine("No se encontró ningún mando conectado.");
            Console.ReadKey();
            return;
        }

        // Intento inicial de conexión
        bool estadoBluetooth = ConectarBluetoothSilencioso(nombrePuerto, baudios);
        DateTime ultimoIntentoReconexion = DateTime.Now;

        Console.Clear();
        Console.WriteLine("Mando PS3 detectado correctamente.");
        Console.WriteLine("Presiona los botones o mueve el joystick (ESC para salir):\n");

        while (true)
        {
            // --- LÓGICA DE RECONEXIÓN AUTOMÁTICA ---
            if (!estadoBluetooth || puertoSerial == null || !puertoSerial.IsOpen)
            {
                estadoBluetooth = false;
                // Reintentar conexión cada 2 segundos
                if ((DateTime.Now - ultimoIntentoReconexion).TotalSeconds >= 2.0)
                {
                    ultimoIntentoReconexion = DateTime.Now;
                    estadoBluetooth = ConectarBluetoothSilencioso(nombrePuerto, baudios);
                }
            }

            State state = controller.GetState();
            Gamepad gamepad = state.Gamepad;

            double x = gamepad.LeftThumbX;
            double y = gamepad.LeftThumbY;

            // --- BLOQUEO DE LÍNEA RECTA CON R1 ---
            bool modoLineaRecta = gamepad.Buttons.HasFlag(GamepadButtonFlags.RightShoulder);
            if (modoLineaRecta)
            {
                x = 0; // Se anula la componente horizontal por completo
            }

            // --- CORRECCIÓN DE GIRO EN REVERSA ---
            // Si el joystick apunta hacia atrás (y < 0), invertimos X para que el giro coincida con la perspectiva natural
            if (y < 0)
            {
                x = -x;
            }

            // 1. Convertir entrada del Joystick a Coordenadas Polares (Magnitud y Ángulo)
            double magnitudBruta = Math.Sqrt(x * x + y * y);
            int motorIzq = 0;
            int motorDer = 0;
            double anguloGrados = 0;

            // Filtro de Zona Muerta solo para el centro del joystick (holgura física)
            if (magnitudBruta > 7000)
            {
                // Escalado proporcional de velocidad de 0 a 255 basado en qué tan empujado está el joystick
                double velocidad = ((magnitudBruta - 7000.0) / (32767.0 - 7000.0)) * 255.0;
                velocidad = Math.Clamp(velocidad, 0.0, 255.0);

                // Cálculo del ángulo exacto del joystick (en Radianes)
                double anguloRad = Math.Atan2(y, x);

                // Convertir a grados para telemetría
                anguloGrados = anguloRad * (180.0 / Math.PI);
                if (anguloGrados < 0) anguloGrados += 360.0;

                // --- FÓRMULA DIFERENCIAL POLAR ---
                double tempIzq = Math.Sin(anguloRad) - Math.Cos(anguloRad);
                double tempDer = Math.Sin(anguloRad) + Math.Cos(anguloRad);

                // Normalización de forma para mantener velocidad consistente en diagonales
                double maxForma = Math.Max(Math.Abs(tempIzq), Math.Abs(tempDer));
                if (maxForma > 0)
                {
                    tempIzq /= maxForma;
                    tempDer /= maxForma;
                }

                // Escalado final según la inclinación del joystick
                motorIzq = (int)Math.Round(tempIzq * velocidad);
                motorDer = (int)Math.Round(tempDer * velocidad);

                // Limpieza de rangos estricta
                motorIzq = Math.Clamp(motorIzq, -255, 255);
                motorDer = Math.Clamp(motorDer, -255, 255);
            }

            // 2. Envío de datos al Robot
            string tramaEnviar = $"M,{motorIzq},{motorDer}\n";
            if (estadoBluetooth && puertoSerial != null && puertoSerial.IsOpen)
            {
                try
                {
                    puertoSerial.Write(tramaEnviar);
                }
                catch
                {
                    // Pérdida de señal o caída momentánea:
                    estadoBluetooth = false;
                    CerrarPuertoSeguro();
                }
            }

            // 3. Impresión en pantalla
            Console.SetCursorPosition(0, 3);

            Console.WriteLine($"Joystick Izquierdo X : {gamepad.LeftThumbX,-10}");
            Console.WriteLine($"Joystick Izquierdo Y : {gamepad.LeftThumbY,-10}");
            Console.WriteLine($"Joystick Derecho X   : {gamepad.RightThumbX,-10}");
            Console.WriteLine($"Joystick Derecho Y   : {gamepad.RightThumbY,-10}");

            Console.WriteLine($"Gatillo L2           : {gamepad.LeftTrigger,-10}");
            Console.WriteLine($"Gatillo R2           : {gamepad.RightTrigger,-10}");

            string botonesPresionados = ObtenerBotonesPS3(gamepad.Buttons);
            Console.WriteLine($"Botones Activos      : {botonesPresionados,-40}");

            // --- TELEMETRÍA Y ESTADO DE CONEXIÓN ---
            Console.WriteLine("\n--------------------------------------------------");
            string textoEstado = estadoBluetooth
                ? "CONECTADO A " + nombrePuerto
                : "RECONECTANDO... (Asegura encendido)";

            Console.WriteLine($"Estado Conexión      : {textoEstado,-35}");
            Console.WriteLine($"Modo Recto (R1)      : {(modoLineaRecta ? "[ACTIVO]" : "Inactivo"),-35}");
            Console.WriteLine($"Ángulo Atan2         : {anguloGrados,5:F1}°");
            Console.WriteLine($"Magnitud             : {magnitudBruta,5:F0}");
            Console.WriteLine($"PWM Motor Izquierdo  : {motorIzq,-10}");
            Console.WriteLine($"PWM Motor Derecho    : {motorDer,-10}");
            Console.WriteLine($"Trama Enviada        : {tramaEnviar.Trim(),-20}");

            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                break;

            Thread.Sleep(30); // Refresco óptimo para evitar saturación del HC-05
        }

        // Cierre seguro al salir
        CerrarPuertoSeguro();
    }

    static bool ConectarBluetoothSilencioso(string puerto, int baudios)
    {
        try
        {
            CerrarPuertoSeguro();
            puertoSerial = new SerialPort(puerto, baudios);
            puertoSerial.ReadTimeout = 50;
            puertoSerial.WriteTimeout = 50;
            puertoSerial.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void CerrarPuertoSeguro()
    {
        try
        {
            if (puertoSerial != null)
            {
                if (puertoSerial.IsOpen)
                {
                    puertoSerial.Write("M,0,0\n");
                    puertoSerial.Close();
                }
                puertoSerial.Dispose();
                puertoSerial = null;
            }
        }
        catch { }
    }

    static string ObtenerBotonesPS3(GamepadButtonFlags flags)
    {
        if (flags == GamepadButtonFlags.None) return "Ninguno";

        var botones = new List<string>();

        if (flags.HasFlag(GamepadButtonFlags.A)) botones.Add("X (Cruz)");
        if (flags.HasFlag(GamepadButtonFlags.B)) botones.Add("Círculo");
        if (flags.HasFlag(GamepadButtonFlags.X)) botones.Add("Cuadrado");
        if (flags.HasFlag(GamepadButtonFlags.Y)) botones.Add("Triángulo");

        if (flags.HasFlag(GamepadButtonFlags.LeftShoulder)) botones.Add("L1");
        if (flags.HasFlag(GamepadButtonFlags.RightShoulder)) botones.Add("R1");
        if (flags.HasFlag(GamepadButtonFlags.LeftThumb)) botones.Add("L3");
        if (flags.HasFlag(GamepadButtonFlags.RightThumb)) botones.Add("R3");

        if (flags.HasFlag(GamepadButtonFlags.Start)) botones.Add("START");
        if (flags.HasFlag(GamepadButtonFlags.Back)) botones.Add("SELECT");

        if (flags.HasFlag(GamepadButtonFlags.DPadUp)) botones.Add("D-Pad Arriba");
        if (flags.HasFlag(GamepadButtonFlags.DPadDown)) botones.Add("D-Pad Abajo");
        if (flags.HasFlag(GamepadButtonFlags.DPadLeft)) botones.Add("D-Pad Izquierda");
        if (flags.HasFlag(GamepadButtonFlags.DPadRight)) botones.Add("D-Pad Derecha");

        return string.Join(" + ", botones);
    }
}

/*CODIGO FUNCIONAL SENSIBLE A GIROS*/

//using System;
//using System.Collections.Generic;
//using System.IO.Ports;
//using System.Threading;
//using SharpDX.XInput;

//class Program
//{
//    static SerialPort puertoSerial;
//    static string nombrePuerto = "COM10";
//    static int baudios = 9600;

//    static void Main()
//    {
//        var controller = new Controller(UserIndex.One);

//        if (!controller.IsConnected)
//        {
//            Console.WriteLine("No se encontró ningún mando conectado.");
//            Console.ReadKey();
//            return;
//        }

//        // Intento inicial de conexión
//        bool estadoBluetooth = ConectarBluetoothSilencioso(nombrePuerto, baudios);
//        DateTime ultimoIntentoReconexion = DateTime.Now;

//        Console.Clear();
//        Console.WriteLine("Mando PS3 detectado correctamente.");
//        Console.WriteLine("Presiona los botones o mueve el joystick (ESC para salir):\n");

//        while (true)
//        {
//            // --- LÓGICA DE RECONEXIÓN AUTOMÁTICA ---
//            if (!estadoBluetooth || puertoSerial == null || !puertoSerial.IsOpen)
//            {
//                estadoBluetooth = false;
//                // Reintentar conexión cada 2 segundos
//                if ((DateTime.Now - ultimoIntentoReconexion).TotalSeconds >= 2.0)
//                {
//                    ultimoIntentoReconexion = DateTime.Now;
//                    estadoBluetooth = ConectarBluetoothSilencioso(nombrePuerto, baudios);
//                }
//            }

//            State state = controller.GetState();
//            Gamepad gamepad = state.Gamepad;

//            double x = gamepad.LeftThumbX;
//            double y = gamepad.LeftThumbY;

//            // 1. Convertir entrada del Joystick a Coordenadas Polares (Magnitud y Ángulo)
//            double magnitudBruta = Math.Sqrt(x * x + y * y);
//            int motorIzq = 0;
//            int motorDer = 0;
//            double anguloGrados = 0;

//            // Filtro de Zona Muerta solo para el centro del joystick (holgura física)
//            if (magnitudBruta > 7000)
//            {
//                // Escalado proporcional de velocidad de 0 a 255 basado en qué tan empujado está el joystick
//                double velocidad = ((magnitudBruta - 7000.0) / (32767.0 - 7000.0)) * 255.0;
//                velocidad = Math.Clamp(velocidad, 0.0, 255.0);

//                // Cálculo del ángulo exacto del joystick (en Radianes)
//                double anguloRad = Math.Atan2(y, x);

//                // Convertir a grados para telemetría
//                anguloGrados = anguloRad * (180.0 / Math.PI);
//                if (anguloGrados < 0) anguloGrados += 360.0;

//                // --- FÓRMULA DIFERENCIAL POLAR (DIRECCIÓN IZQ/DER CORREGIDA) ---
//                // Al restar Cos(angulo) en tempIzq y sumar en tempDer, invertimos el sentido horizontal
//                double tempIzq = Math.Sin(anguloRad) - Math.Cos(anguloRad);
//                double tempDer = Math.Sin(anguloRad) + Math.Cos(anguloRad);

//                // Normalización de forma para mantener velocidad consistente en diagonales
//                double maxForma = Math.Max(Math.Abs(tempIzq), Math.Abs(tempDer));
//                if (maxForma > 0)
//                {
//                    tempIzq /= maxForma;
//                    tempDer /= maxForma;
//                }

//                // Escalado final según la inclinación del joystick
//                motorIzq = (int)Math.Round(tempIzq * velocidad);
//                motorDer = (int)Math.Round(tempDer * velocidad);

//                // Limpieza de rangos estricta
//                motorIzq = Math.Clamp(motorIzq, -255, 255);
//                motorDer = Math.Clamp(motorDer, -255, 255);
//            }

//            // 2. Envío de datos al Robot
//            string tramaEnviar = $"M,{motorIzq},{motorDer}\n";
//            if (estadoBluetooth && puertoSerial != null && puertoSerial.IsOpen)
//            {
//                try
//                {
//                    puertoSerial.Write(tramaEnviar);
//                }
//                catch
//                {
//                    // Pérdida de señal o caída momentánea:
//                    estadoBluetooth = false;
//                    CerrarPuertoSeguro();
//                }
//            }

//            // 3. Impresión en pantalla
//            Console.SetCursorPosition(0, 3);

//            Console.WriteLine($"Joystick Izquierdo X : {gamepad.LeftThumbX,-10}");
//            Console.WriteLine($"Joystick Izquierdo Y : {gamepad.LeftThumbY,-10}");
//            Console.WriteLine($"Joystick Derecho X   : {gamepad.RightThumbX,-10}");
//            Console.WriteLine($"Joystick Derecho Y   : {gamepad.RightThumbY,-10}");

//            Console.WriteLine($"Gatillo L2           : {gamepad.LeftTrigger,-10}");
//            Console.WriteLine($"Gatillo R2           : {gamepad.RightTrigger,-10}");

//            string botonesPresionados = ObtenerBotonesPS3(gamepad.Buttons);
//            Console.WriteLine($"Botones Activos      : {botonesPresionados,-40}");

//            // --- TELEMETRÍA Y ESTADO DE CONEXIÓN ---
//            Console.WriteLine("\n--------------------------------------------------");
//            string textoEstado = estadoBluetooth
//                ? "CONECTADO A " + nombrePuerto
//                : "RECONECTANDO... (Asegura encendido)";

//            Console.WriteLine($"Estado Conexión      : {textoEstado,-35}");
//            Console.WriteLine($"Ángulo Atan2         : {anguloGrados,5:F1}°");
//            Console.WriteLine($"Magnitud             : {magnitudBruta,5:F0}");
//            Console.WriteLine($"PWM Motor Izquierdo  : {motorIzq,-10}");
//            Console.WriteLine($"PWM Motor Derecho    : {motorDer,-10}");
//            Console.WriteLine($"Trama Enviada        : {tramaEnviar.Trim(),-20}");

//            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
//                break;

//            Thread.Sleep(30); // Refresco óptimo para evitar saturación del HC-05
//        }

//        // Cierre seguro al salir
//        CerrarPuertoSeguro();
//    }

//    static bool ConectarBluetoothSilencioso(string puerto, int baudios)
//    {
//        try
//        {
//            CerrarPuertoSeguro();
//            puertoSerial = new SerialPort(puerto, baudios);
//            puertoSerial.ReadTimeout = 50;
//            puertoSerial.WriteTimeout = 50;
//            puertoSerial.Open();
//            return true;
//        }
//        catch
//        {
//            return false;
//        }
//    }

//    static void CerrarPuertoSeguro()
//    {
//        try
//        {
//            if (puertoSerial != null)
//            {
//                if (puertoSerial.IsOpen)
//                {
//                    puertoSerial.Write("M,0,0\n");
//                    puertoSerial.Close();
//                }
//                puertoSerial.Dispose();
//                puertoSerial = null;
//            }
//        }
//        catch { }
//    }

//    static string ObtenerBotonesPS3(GamepadButtonFlags flags)
//    {
//        if (flags == GamepadButtonFlags.None) return "Ninguno";

//        var botones = new List<string>();

//        if (flags.HasFlag(GamepadButtonFlags.A)) botones.Add("X (Cruz)");
//        if (flags.HasFlag(GamepadButtonFlags.B)) botones.Add("Círculo");
//        if (flags.HasFlag(GamepadButtonFlags.X)) botones.Add("Cuadrado");
//        if (flags.HasFlag(GamepadButtonFlags.Y)) botones.Add("Triángulo");

//        if (flags.HasFlag(GamepadButtonFlags.LeftShoulder)) botones.Add("L1");
//        if (flags.HasFlag(GamepadButtonFlags.RightShoulder)) botones.Add("R1");
//        if (flags.HasFlag(GamepadButtonFlags.LeftThumb)) botones.Add("L3");
//        if (flags.HasFlag(GamepadButtonFlags.RightThumb)) botones.Add("R3");

//        if (flags.HasFlag(GamepadButtonFlags.Start)) botones.Add("START");
//        if (flags.HasFlag(GamepadButtonFlags.Back)) botones.Add("SELECT");

//        if (flags.HasFlag(GamepadButtonFlags.DPadUp)) botones.Add("D-Pad Arriba");
//        if (flags.HasFlag(GamepadButtonFlags.DPadDown)) botones.Add("D-Pad Abajo");
//        if (flags.HasFlag(GamepadButtonFlags.DPadLeft)) botones.Add("D-Pad Izquierda");
//        if (flags.HasFlag(GamepadButtonFlags.DPadRight)) botones.Add("D-Pad Derecha");

//        return string.Join(" + ", botones);
//    }
//}

