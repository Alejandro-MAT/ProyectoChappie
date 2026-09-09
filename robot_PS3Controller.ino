#include <SoftwareSerial.h>

// Configuración de Bluetooth:
// Pin 9 de Arduino (RX) = Conectado al TXD del HC-05
// Pin 8 de Arduino (TX) = Conectado al RXD del HC-05
SoftwareSerial bluetooth(9, 8); 

// Pines de Velocidad PWM (jumpers ENA y ENB retirados)
const int ENA = 3;  // PWM Motor Izquierdo
const int ENB = 5;  // PWM Motor Derecho

// Pines de Dirección:
const int IN1 = 10; // Motor Derecho
const int IN2 = 11; // Motor Derecho
const int IN3 = 12; // Motor Izquierdo
const int IN4 = 13; // Motor Izquierdo

void setup() {
  pinMode(ENA, OUTPUT); analogWrite(ENA, 0);
  pinMode(ENB, OUTPUT); analogWrite(ENB, 0);
  
  pinMode(IN1, OUTPUT); digitalWrite(IN1, LOW);
  pinMode(IN2, OUTPUT); digitalWrite(IN2, LOW);
  pinMode(IN3, OUTPUT); digitalWrite(IN3, LOW);
  pinMode(IN4, OUTPUT); digitalWrite(IN4, LOW);

  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, LOW);

  bluetooth.begin(9600);
  bluetooth.setTimeout(10); // Lectura rápida para no perder tramas de giro
}

void loop() {
  if (bluetooth.available() > 0) {
    String trama = bluetooth.readStringUntil('\n');

    if (trama.startsWith("M,")) {
      int primeraComa = trama.indexOf(',');
      int segundaComa = trama.indexOf(',', primeraComa + 1);

      if (primeraComa != -1 && segundaComa != -1) {
        int valIzq = trama.substring(primeraComa + 1, segundaComa).toInt();
        int valDer = trama.substring(segundaComa + 1).toInt();

        // Encender LED si hay movimiento
        digitalWrite(LED_BUILTIN, (valIzq != 0 || valDer != 0) ? HIGH : LOW);

        // Control de motores
        controlarMotor(ENA, IN3, IN4, valIzq); // Motor Izquierdo
        controlarMotor(ENB, IN1, IN2, valDer); // Motor Derecho
      }
    }
  }
}

void controlarMotor(int pinPWM, int pinIn1, int pinIn2, int velocidad) {
  // Invertimos la lógica (> 0 ahora activa marcha adelante correcta)
  if (velocidad > 0) {
    digitalWrite(pinIn1, LOW);
    digitalWrite(pinIn2, HIGH);
    analogWrite(pinPWM, velocidad);
  } 
  else if (velocidad < 0) {
    digitalWrite(pinIn1, HIGH);
    digitalWrite(pinIn2, LOW);
    analogWrite(pinPWM, abs(velocidad));
  } 
  else {
    digitalWrite(pinIn1, LOW);
    digitalWrite(pinIn2, LOW);
    analogWrite(pinPWM, 0);
  }
}