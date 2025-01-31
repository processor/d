﻿namespace E.Units;

public static class DataUnits
{
    public static readonly UnitInfo Bit      = new(UnitType.Bit,  Dimension.AmountOfInformation, "bit");
    public static readonly UnitInfo Baud     = new(UnitType.Baud, Dimension.AmountOfInformation, "Bd"); // 1bit /s

    public static readonly UnitInfo Byte     = new(UnitType.Byte,     Dimension.AmountOfInformation, "B",   new ConversionFactor(8,                Bit));        
    public static readonly UnitInfo Kibibyte = new(UnitType.Kibibyte, Dimension.AmountOfInformation, "KiB", new ConversionFactor(1_024,            Byte));
    public static readonly UnitInfo Mebibyte = new(UnitType.Mebibyte, Dimension.AmountOfInformation, "MiB", new ConversionFactor(1_048_576,        Byte));
    public static readonly UnitInfo Tebibyte = new(UnitType.Tebibyte, Dimension.AmountOfInformation, "TiB", new ConversionFactor(109_951_1627_776, Byte));
}