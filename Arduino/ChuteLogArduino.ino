

#include <Wire.h>
#include <MS5611.h>

MS5611 ms5611;

typedef union {
 float floatingPoint;
 byte binary[4];
} binaryFloat;

double referencePressure;
char c;
binaryFloat hi;
long realPressure;

void setup() 
{
  Serial.begin(9600);

  while(!ms5611.begin())
  {
    delay(500);
  }
  resetReferenceAltitude();
}

void loop() {
  realPressure = ms5611.readPressure();
  // Calculate altitude
  //float absoluteAltitude = ms5611.getAltitude(realPressure);
  //float relativeAltitude = ms5611.getAltitude(realPressure, referencePressure);
  hi.floatingPoint = ms5611.getAltitude(realPressure, referencePressure);
  if(Serial.available())
  {
   c = Serial.read();
   if(c=='A')
     sendAltitude();
   else if(c=='R')
     resetReferenceAltitude();
  }
  delay(100);
}

void resetReferenceAltitude()
{
  // Get reference pressure for relative altitude
  referencePressure = ms5611.readPressure();
}

void sendAltitude()
{
 Serial.write(hi.binary,4);
}

