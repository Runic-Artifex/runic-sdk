// Written by tests/scaling/generate-fixtures.mjs: edit the script, not this file.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using Runic.Application.Views;

namespace Runic.Application.Scaling.Tests;

public sealed class Wide20ToolkitViewModel : ObservableObject, IWideViewModel
{
    private string _m00 = "";
    private int _m01;
    private string _m02 = "";
    private int _m03;
    private string _m04 = "";
    private int _m05;
    private string _m06 = "";
    private int _m07;
    private string _m08 = "";
    private int _m09;
    private string _m10 = "";
    private int _m11;
    private string _m12 = "";
    private int _m13;
    private string _m14 = "";
    private int _m15;
    private string _m16 = "";
    private int _m17;
    private string _m18 = "";
    private int _m19;

    [RunicIgnore] public GetterCounts Counts { get; } = new(20);
    [RunicIgnore] public int Size => 20;

    public string M00 { get { Counts.Read(0); return _m00; } set => SetProperty(ref _m00, value); }
    public int M01 { get { Counts.Read(1); return _m01; } set => SetProperty(ref _m01, value); }
    public string M02 { get { Counts.Read(2); return _m02; } set => SetProperty(ref _m02, value); }
    public int M03 { get { Counts.Read(3); return _m03; } set => SetProperty(ref _m03, value); }
    public string M04 { get { Counts.Read(4); return _m04; } set => SetProperty(ref _m04, value); }
    public int M05 { get { Counts.Read(5); return _m05; } set => SetProperty(ref _m05, value); }
    public string M06 { get { Counts.Read(6); return _m06; } set => SetProperty(ref _m06, value); }
    public int M07 { get { Counts.Read(7); return _m07; } set => SetProperty(ref _m07, value); }
    public string M08 { get { Counts.Read(8); return _m08; } set => SetProperty(ref _m08, value); }
    public int M09 { get { Counts.Read(9); return _m09; } set => SetProperty(ref _m09, value); }
    public string M10 { get { Counts.Read(10); return _m10; } set => SetProperty(ref _m10, value); }
    public int M11 { get { Counts.Read(11); return _m11; } set => SetProperty(ref _m11, value); }
    public string M12 { get { Counts.Read(12); return _m12; } set => SetProperty(ref _m12, value); }
    public int M13 { get { Counts.Read(13); return _m13; } set => SetProperty(ref _m13, value); }
    public string M14 { get { Counts.Read(14); return _m14; } set => SetProperty(ref _m14, value); }
    public int M15 { get { Counts.Read(15); return _m15; } set => SetProperty(ref _m15, value); }
    public string M16 { get { Counts.Read(16); return _m16; } set => SetProperty(ref _m16, value); }
    public int M17 { get { Counts.Read(17); return _m17; } set => SetProperty(ref _m17, value); }
    public string M18 { get { Counts.Read(18); return _m18; } set => SetProperty(ref _m18, value); }
    public int M19 { get { Counts.Read(19); return _m19; } set => SetProperty(ref _m19, value); }

    public void SetScalar(int index, int value)
    {
        switch (index)
        {
            case 0: M00 = value.ToString(CultureInfo.InvariantCulture); break;
            case 1: M01 = value; break;
            case 2: M02 = value.ToString(CultureInfo.InvariantCulture); break;
            case 3: M03 = value; break;
            case 4: M04 = value.ToString(CultureInfo.InvariantCulture); break;
            case 5: M05 = value; break;
            case 6: M06 = value.ToString(CultureInfo.InvariantCulture); break;
            case 7: M07 = value; break;
            case 8: M08 = value.ToString(CultureInfo.InvariantCulture); break;
            case 9: M09 = value; break;
            case 10: M10 = value.ToString(CultureInfo.InvariantCulture); break;
            case 11: M11 = value; break;
            case 12: M12 = value.ToString(CultureInfo.InvariantCulture); break;
            case 13: M13 = value; break;
            case 14: M14 = value.ToString(CultureInfo.InvariantCulture); break;
            case 15: M15 = value; break;
            case 16: M16 = value.ToString(CultureInfo.InvariantCulture); break;
            case 17: M17 = value; break;
            case 18: M18 = value.ToString(CultureInfo.InvariantCulture); break;
            case 19: M19 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, null);
        }
    }

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));
}

public sealed partial class Wide20ToolkitWindow : RunicWindow<Wide20ToolkitViewModel>;

public sealed class Wide20ReactiveViewModel : ReactiveObject, IWideViewModel
{
    private string _m00 = "";
    private int _m01;
    private string _m02 = "";
    private int _m03;
    private string _m04 = "";
    private int _m05;
    private string _m06 = "";
    private int _m07;
    private string _m08 = "";
    private int _m09;
    private string _m10 = "";
    private int _m11;
    private string _m12 = "";
    private int _m13;
    private string _m14 = "";
    private int _m15;
    private string _m16 = "";
    private int _m17;
    private string _m18 = "";
    private int _m19;

    [RunicIgnore] public GetterCounts Counts { get; } = new(20);
    [RunicIgnore] public int Size => 20;

    public string M00 { get { Counts.Read(0); return _m00; } set => this.RaiseAndSetIfChanged(ref _m00, value); }
    public int M01 { get { Counts.Read(1); return _m01; } set => this.RaiseAndSetIfChanged(ref _m01, value); }
    public string M02 { get { Counts.Read(2); return _m02; } set => this.RaiseAndSetIfChanged(ref _m02, value); }
    public int M03 { get { Counts.Read(3); return _m03; } set => this.RaiseAndSetIfChanged(ref _m03, value); }
    public string M04 { get { Counts.Read(4); return _m04; } set => this.RaiseAndSetIfChanged(ref _m04, value); }
    public int M05 { get { Counts.Read(5); return _m05; } set => this.RaiseAndSetIfChanged(ref _m05, value); }
    public string M06 { get { Counts.Read(6); return _m06; } set => this.RaiseAndSetIfChanged(ref _m06, value); }
    public int M07 { get { Counts.Read(7); return _m07; } set => this.RaiseAndSetIfChanged(ref _m07, value); }
    public string M08 { get { Counts.Read(8); return _m08; } set => this.RaiseAndSetIfChanged(ref _m08, value); }
    public int M09 { get { Counts.Read(9); return _m09; } set => this.RaiseAndSetIfChanged(ref _m09, value); }
    public string M10 { get { Counts.Read(10); return _m10; } set => this.RaiseAndSetIfChanged(ref _m10, value); }
    public int M11 { get { Counts.Read(11); return _m11; } set => this.RaiseAndSetIfChanged(ref _m11, value); }
    public string M12 { get { Counts.Read(12); return _m12; } set => this.RaiseAndSetIfChanged(ref _m12, value); }
    public int M13 { get { Counts.Read(13); return _m13; } set => this.RaiseAndSetIfChanged(ref _m13, value); }
    public string M14 { get { Counts.Read(14); return _m14; } set => this.RaiseAndSetIfChanged(ref _m14, value); }
    public int M15 { get { Counts.Read(15); return _m15; } set => this.RaiseAndSetIfChanged(ref _m15, value); }
    public string M16 { get { Counts.Read(16); return _m16; } set => this.RaiseAndSetIfChanged(ref _m16, value); }
    public int M17 { get { Counts.Read(17); return _m17; } set => this.RaiseAndSetIfChanged(ref _m17, value); }
    public string M18 { get { Counts.Read(18); return _m18; } set => this.RaiseAndSetIfChanged(ref _m18, value); }
    public int M19 { get { Counts.Read(19); return _m19; } set => this.RaiseAndSetIfChanged(ref _m19, value); }

    public void SetScalar(int index, int value)
    {
        switch (index)
        {
            case 0: M00 = value.ToString(CultureInfo.InvariantCulture); break;
            case 1: M01 = value; break;
            case 2: M02 = value.ToString(CultureInfo.InvariantCulture); break;
            case 3: M03 = value; break;
            case 4: M04 = value.ToString(CultureInfo.InvariantCulture); break;
            case 5: M05 = value; break;
            case 6: M06 = value.ToString(CultureInfo.InvariantCulture); break;
            case 7: M07 = value; break;
            case 8: M08 = value.ToString(CultureInfo.InvariantCulture); break;
            case 9: M09 = value; break;
            case 10: M10 = value.ToString(CultureInfo.InvariantCulture); break;
            case 11: M11 = value; break;
            case 12: M12 = value.ToString(CultureInfo.InvariantCulture); break;
            case 13: M13 = value; break;
            case 14: M14 = value.ToString(CultureInfo.InvariantCulture); break;
            case 15: M15 = value; break;
            case 16: M16 = value.ToString(CultureInfo.InvariantCulture); break;
            case 17: M17 = value; break;
            case 18: M18 = value.ToString(CultureInfo.InvariantCulture); break;
            case 19: M19 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, null);
        }
    }

    public void Notify(string? member) => this.RaisePropertyChanged(member ?? "");
}

public sealed partial class Wide20ReactiveWindow : RunicWindow<Wide20ReactiveViewModel>;

public sealed class Wide80ToolkitViewModel : ObservableObject, IWideViewModel
{
    private string _m00 = "";
    private int _m01;
    private string _m02 = "";
    private int _m03;
    private string _m04 = "";
    private int _m05;
    private string _m06 = "";
    private int _m07;
    private string _m08 = "";
    private int _m09;
    private string _m10 = "";
    private int _m11;
    private string _m12 = "";
    private int _m13;
    private string _m14 = "";
    private int _m15;
    private string _m16 = "";
    private int _m17;
    private string _m18 = "";
    private int _m19;
    private string _m20 = "";
    private int _m21;
    private string _m22 = "";
    private int _m23;
    private string _m24 = "";
    private int _m25;
    private string _m26 = "";
    private int _m27;
    private string _m28 = "";
    private int _m29;
    private string _m30 = "";
    private int _m31;
    private string _m32 = "";
    private int _m33;
    private string _m34 = "";
    private int _m35;
    private string _m36 = "";
    private int _m37;
    private string _m38 = "";
    private int _m39;
    private string _m40 = "";
    private int _m41;
    private string _m42 = "";
    private int _m43;
    private string _m44 = "";
    private int _m45;
    private string _m46 = "";
    private int _m47;
    private string _m48 = "";
    private int _m49;
    private string _m50 = "";
    private int _m51;
    private string _m52 = "";
    private int _m53;
    private string _m54 = "";
    private int _m55;
    private string _m56 = "";
    private int _m57;
    private string _m58 = "";
    private int _m59;
    private string _m60 = "";
    private int _m61;
    private string _m62 = "";
    private int _m63;
    private string _m64 = "";
    private int _m65;
    private string _m66 = "";
    private int _m67;
    private string _m68 = "";
    private int _m69;
    private string _m70 = "";
    private int _m71;
    private string _m72 = "";
    private int _m73;
    private string _m74 = "";
    private int _m75;
    private string _m76 = "";
    private int _m77;
    private string _m78 = "";
    private int _m79;

    [RunicIgnore] public GetterCounts Counts { get; } = new(80);
    [RunicIgnore] public int Size => 80;

    public string M00 { get { Counts.Read(0); return _m00; } set => SetProperty(ref _m00, value); }
    public int M01 { get { Counts.Read(1); return _m01; } set => SetProperty(ref _m01, value); }
    public string M02 { get { Counts.Read(2); return _m02; } set => SetProperty(ref _m02, value); }
    public int M03 { get { Counts.Read(3); return _m03; } set => SetProperty(ref _m03, value); }
    public string M04 { get { Counts.Read(4); return _m04; } set => SetProperty(ref _m04, value); }
    public int M05 { get { Counts.Read(5); return _m05; } set => SetProperty(ref _m05, value); }
    public string M06 { get { Counts.Read(6); return _m06; } set => SetProperty(ref _m06, value); }
    public int M07 { get { Counts.Read(7); return _m07; } set => SetProperty(ref _m07, value); }
    public string M08 { get { Counts.Read(8); return _m08; } set => SetProperty(ref _m08, value); }
    public int M09 { get { Counts.Read(9); return _m09; } set => SetProperty(ref _m09, value); }
    public string M10 { get { Counts.Read(10); return _m10; } set => SetProperty(ref _m10, value); }
    public int M11 { get { Counts.Read(11); return _m11; } set => SetProperty(ref _m11, value); }
    public string M12 { get { Counts.Read(12); return _m12; } set => SetProperty(ref _m12, value); }
    public int M13 { get { Counts.Read(13); return _m13; } set => SetProperty(ref _m13, value); }
    public string M14 { get { Counts.Read(14); return _m14; } set => SetProperty(ref _m14, value); }
    public int M15 { get { Counts.Read(15); return _m15; } set => SetProperty(ref _m15, value); }
    public string M16 { get { Counts.Read(16); return _m16; } set => SetProperty(ref _m16, value); }
    public int M17 { get { Counts.Read(17); return _m17; } set => SetProperty(ref _m17, value); }
    public string M18 { get { Counts.Read(18); return _m18; } set => SetProperty(ref _m18, value); }
    public int M19 { get { Counts.Read(19); return _m19; } set => SetProperty(ref _m19, value); }
    public string M20 { get { Counts.Read(20); return _m20; } set => SetProperty(ref _m20, value); }
    public int M21 { get { Counts.Read(21); return _m21; } set => SetProperty(ref _m21, value); }
    public string M22 { get { Counts.Read(22); return _m22; } set => SetProperty(ref _m22, value); }
    public int M23 { get { Counts.Read(23); return _m23; } set => SetProperty(ref _m23, value); }
    public string M24 { get { Counts.Read(24); return _m24; } set => SetProperty(ref _m24, value); }
    public int M25 { get { Counts.Read(25); return _m25; } set => SetProperty(ref _m25, value); }
    public string M26 { get { Counts.Read(26); return _m26; } set => SetProperty(ref _m26, value); }
    public int M27 { get { Counts.Read(27); return _m27; } set => SetProperty(ref _m27, value); }
    public string M28 { get { Counts.Read(28); return _m28; } set => SetProperty(ref _m28, value); }
    public int M29 { get { Counts.Read(29); return _m29; } set => SetProperty(ref _m29, value); }
    public string M30 { get { Counts.Read(30); return _m30; } set => SetProperty(ref _m30, value); }
    public int M31 { get { Counts.Read(31); return _m31; } set => SetProperty(ref _m31, value); }
    public string M32 { get { Counts.Read(32); return _m32; } set => SetProperty(ref _m32, value); }
    public int M33 { get { Counts.Read(33); return _m33; } set => SetProperty(ref _m33, value); }
    public string M34 { get { Counts.Read(34); return _m34; } set => SetProperty(ref _m34, value); }
    public int M35 { get { Counts.Read(35); return _m35; } set => SetProperty(ref _m35, value); }
    public string M36 { get { Counts.Read(36); return _m36; } set => SetProperty(ref _m36, value); }
    public int M37 { get { Counts.Read(37); return _m37; } set => SetProperty(ref _m37, value); }
    public string M38 { get { Counts.Read(38); return _m38; } set => SetProperty(ref _m38, value); }
    public int M39 { get { Counts.Read(39); return _m39; } set => SetProperty(ref _m39, value); }
    public string M40 { get { Counts.Read(40); return _m40; } set => SetProperty(ref _m40, value); }
    public int M41 { get { Counts.Read(41); return _m41; } set => SetProperty(ref _m41, value); }
    public string M42 { get { Counts.Read(42); return _m42; } set => SetProperty(ref _m42, value); }
    public int M43 { get { Counts.Read(43); return _m43; } set => SetProperty(ref _m43, value); }
    public string M44 { get { Counts.Read(44); return _m44; } set => SetProperty(ref _m44, value); }
    public int M45 { get { Counts.Read(45); return _m45; } set => SetProperty(ref _m45, value); }
    public string M46 { get { Counts.Read(46); return _m46; } set => SetProperty(ref _m46, value); }
    public int M47 { get { Counts.Read(47); return _m47; } set => SetProperty(ref _m47, value); }
    public string M48 { get { Counts.Read(48); return _m48; } set => SetProperty(ref _m48, value); }
    public int M49 { get { Counts.Read(49); return _m49; } set => SetProperty(ref _m49, value); }
    public string M50 { get { Counts.Read(50); return _m50; } set => SetProperty(ref _m50, value); }
    public int M51 { get { Counts.Read(51); return _m51; } set => SetProperty(ref _m51, value); }
    public string M52 { get { Counts.Read(52); return _m52; } set => SetProperty(ref _m52, value); }
    public int M53 { get { Counts.Read(53); return _m53; } set => SetProperty(ref _m53, value); }
    public string M54 { get { Counts.Read(54); return _m54; } set => SetProperty(ref _m54, value); }
    public int M55 { get { Counts.Read(55); return _m55; } set => SetProperty(ref _m55, value); }
    public string M56 { get { Counts.Read(56); return _m56; } set => SetProperty(ref _m56, value); }
    public int M57 { get { Counts.Read(57); return _m57; } set => SetProperty(ref _m57, value); }
    public string M58 { get { Counts.Read(58); return _m58; } set => SetProperty(ref _m58, value); }
    public int M59 { get { Counts.Read(59); return _m59; } set => SetProperty(ref _m59, value); }
    public string M60 { get { Counts.Read(60); return _m60; } set => SetProperty(ref _m60, value); }
    public int M61 { get { Counts.Read(61); return _m61; } set => SetProperty(ref _m61, value); }
    public string M62 { get { Counts.Read(62); return _m62; } set => SetProperty(ref _m62, value); }
    public int M63 { get { Counts.Read(63); return _m63; } set => SetProperty(ref _m63, value); }
    public string M64 { get { Counts.Read(64); return _m64; } set => SetProperty(ref _m64, value); }
    public int M65 { get { Counts.Read(65); return _m65; } set => SetProperty(ref _m65, value); }
    public string M66 { get { Counts.Read(66); return _m66; } set => SetProperty(ref _m66, value); }
    public int M67 { get { Counts.Read(67); return _m67; } set => SetProperty(ref _m67, value); }
    public string M68 { get { Counts.Read(68); return _m68; } set => SetProperty(ref _m68, value); }
    public int M69 { get { Counts.Read(69); return _m69; } set => SetProperty(ref _m69, value); }
    public string M70 { get { Counts.Read(70); return _m70; } set => SetProperty(ref _m70, value); }
    public int M71 { get { Counts.Read(71); return _m71; } set => SetProperty(ref _m71, value); }
    public string M72 { get { Counts.Read(72); return _m72; } set => SetProperty(ref _m72, value); }
    public int M73 { get { Counts.Read(73); return _m73; } set => SetProperty(ref _m73, value); }
    public string M74 { get { Counts.Read(74); return _m74; } set => SetProperty(ref _m74, value); }
    public int M75 { get { Counts.Read(75); return _m75; } set => SetProperty(ref _m75, value); }
    public string M76 { get { Counts.Read(76); return _m76; } set => SetProperty(ref _m76, value); }
    public int M77 { get { Counts.Read(77); return _m77; } set => SetProperty(ref _m77, value); }
    public string M78 { get { Counts.Read(78); return _m78; } set => SetProperty(ref _m78, value); }
    public int M79 { get { Counts.Read(79); return _m79; } set => SetProperty(ref _m79, value); }

    public void SetScalar(int index, int value)
    {
        switch (index)
        {
            case 0: M00 = value.ToString(CultureInfo.InvariantCulture); break;
            case 1: M01 = value; break;
            case 2: M02 = value.ToString(CultureInfo.InvariantCulture); break;
            case 3: M03 = value; break;
            case 4: M04 = value.ToString(CultureInfo.InvariantCulture); break;
            case 5: M05 = value; break;
            case 6: M06 = value.ToString(CultureInfo.InvariantCulture); break;
            case 7: M07 = value; break;
            case 8: M08 = value.ToString(CultureInfo.InvariantCulture); break;
            case 9: M09 = value; break;
            case 10: M10 = value.ToString(CultureInfo.InvariantCulture); break;
            case 11: M11 = value; break;
            case 12: M12 = value.ToString(CultureInfo.InvariantCulture); break;
            case 13: M13 = value; break;
            case 14: M14 = value.ToString(CultureInfo.InvariantCulture); break;
            case 15: M15 = value; break;
            case 16: M16 = value.ToString(CultureInfo.InvariantCulture); break;
            case 17: M17 = value; break;
            case 18: M18 = value.ToString(CultureInfo.InvariantCulture); break;
            case 19: M19 = value; break;
            case 20: M20 = value.ToString(CultureInfo.InvariantCulture); break;
            case 21: M21 = value; break;
            case 22: M22 = value.ToString(CultureInfo.InvariantCulture); break;
            case 23: M23 = value; break;
            case 24: M24 = value.ToString(CultureInfo.InvariantCulture); break;
            case 25: M25 = value; break;
            case 26: M26 = value.ToString(CultureInfo.InvariantCulture); break;
            case 27: M27 = value; break;
            case 28: M28 = value.ToString(CultureInfo.InvariantCulture); break;
            case 29: M29 = value; break;
            case 30: M30 = value.ToString(CultureInfo.InvariantCulture); break;
            case 31: M31 = value; break;
            case 32: M32 = value.ToString(CultureInfo.InvariantCulture); break;
            case 33: M33 = value; break;
            case 34: M34 = value.ToString(CultureInfo.InvariantCulture); break;
            case 35: M35 = value; break;
            case 36: M36 = value.ToString(CultureInfo.InvariantCulture); break;
            case 37: M37 = value; break;
            case 38: M38 = value.ToString(CultureInfo.InvariantCulture); break;
            case 39: M39 = value; break;
            case 40: M40 = value.ToString(CultureInfo.InvariantCulture); break;
            case 41: M41 = value; break;
            case 42: M42 = value.ToString(CultureInfo.InvariantCulture); break;
            case 43: M43 = value; break;
            case 44: M44 = value.ToString(CultureInfo.InvariantCulture); break;
            case 45: M45 = value; break;
            case 46: M46 = value.ToString(CultureInfo.InvariantCulture); break;
            case 47: M47 = value; break;
            case 48: M48 = value.ToString(CultureInfo.InvariantCulture); break;
            case 49: M49 = value; break;
            case 50: M50 = value.ToString(CultureInfo.InvariantCulture); break;
            case 51: M51 = value; break;
            case 52: M52 = value.ToString(CultureInfo.InvariantCulture); break;
            case 53: M53 = value; break;
            case 54: M54 = value.ToString(CultureInfo.InvariantCulture); break;
            case 55: M55 = value; break;
            case 56: M56 = value.ToString(CultureInfo.InvariantCulture); break;
            case 57: M57 = value; break;
            case 58: M58 = value.ToString(CultureInfo.InvariantCulture); break;
            case 59: M59 = value; break;
            case 60: M60 = value.ToString(CultureInfo.InvariantCulture); break;
            case 61: M61 = value; break;
            case 62: M62 = value.ToString(CultureInfo.InvariantCulture); break;
            case 63: M63 = value; break;
            case 64: M64 = value.ToString(CultureInfo.InvariantCulture); break;
            case 65: M65 = value; break;
            case 66: M66 = value.ToString(CultureInfo.InvariantCulture); break;
            case 67: M67 = value; break;
            case 68: M68 = value.ToString(CultureInfo.InvariantCulture); break;
            case 69: M69 = value; break;
            case 70: M70 = value.ToString(CultureInfo.InvariantCulture); break;
            case 71: M71 = value; break;
            case 72: M72 = value.ToString(CultureInfo.InvariantCulture); break;
            case 73: M73 = value; break;
            case 74: M74 = value.ToString(CultureInfo.InvariantCulture); break;
            case 75: M75 = value; break;
            case 76: M76 = value.ToString(CultureInfo.InvariantCulture); break;
            case 77: M77 = value; break;
            case 78: M78 = value.ToString(CultureInfo.InvariantCulture); break;
            case 79: M79 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, null);
        }
    }

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));
}

public sealed partial class Wide80ToolkitWindow : RunicWindow<Wide80ToolkitViewModel>;

public sealed class Wide80ReactiveViewModel : ReactiveObject, IWideViewModel
{
    private string _m00 = "";
    private int _m01;
    private string _m02 = "";
    private int _m03;
    private string _m04 = "";
    private int _m05;
    private string _m06 = "";
    private int _m07;
    private string _m08 = "";
    private int _m09;
    private string _m10 = "";
    private int _m11;
    private string _m12 = "";
    private int _m13;
    private string _m14 = "";
    private int _m15;
    private string _m16 = "";
    private int _m17;
    private string _m18 = "";
    private int _m19;
    private string _m20 = "";
    private int _m21;
    private string _m22 = "";
    private int _m23;
    private string _m24 = "";
    private int _m25;
    private string _m26 = "";
    private int _m27;
    private string _m28 = "";
    private int _m29;
    private string _m30 = "";
    private int _m31;
    private string _m32 = "";
    private int _m33;
    private string _m34 = "";
    private int _m35;
    private string _m36 = "";
    private int _m37;
    private string _m38 = "";
    private int _m39;
    private string _m40 = "";
    private int _m41;
    private string _m42 = "";
    private int _m43;
    private string _m44 = "";
    private int _m45;
    private string _m46 = "";
    private int _m47;
    private string _m48 = "";
    private int _m49;
    private string _m50 = "";
    private int _m51;
    private string _m52 = "";
    private int _m53;
    private string _m54 = "";
    private int _m55;
    private string _m56 = "";
    private int _m57;
    private string _m58 = "";
    private int _m59;
    private string _m60 = "";
    private int _m61;
    private string _m62 = "";
    private int _m63;
    private string _m64 = "";
    private int _m65;
    private string _m66 = "";
    private int _m67;
    private string _m68 = "";
    private int _m69;
    private string _m70 = "";
    private int _m71;
    private string _m72 = "";
    private int _m73;
    private string _m74 = "";
    private int _m75;
    private string _m76 = "";
    private int _m77;
    private string _m78 = "";
    private int _m79;

    [RunicIgnore] public GetterCounts Counts { get; } = new(80);
    [RunicIgnore] public int Size => 80;

    public string M00 { get { Counts.Read(0); return _m00; } set => this.RaiseAndSetIfChanged(ref _m00, value); }
    public int M01 { get { Counts.Read(1); return _m01; } set => this.RaiseAndSetIfChanged(ref _m01, value); }
    public string M02 { get { Counts.Read(2); return _m02; } set => this.RaiseAndSetIfChanged(ref _m02, value); }
    public int M03 { get { Counts.Read(3); return _m03; } set => this.RaiseAndSetIfChanged(ref _m03, value); }
    public string M04 { get { Counts.Read(4); return _m04; } set => this.RaiseAndSetIfChanged(ref _m04, value); }
    public int M05 { get { Counts.Read(5); return _m05; } set => this.RaiseAndSetIfChanged(ref _m05, value); }
    public string M06 { get { Counts.Read(6); return _m06; } set => this.RaiseAndSetIfChanged(ref _m06, value); }
    public int M07 { get { Counts.Read(7); return _m07; } set => this.RaiseAndSetIfChanged(ref _m07, value); }
    public string M08 { get { Counts.Read(8); return _m08; } set => this.RaiseAndSetIfChanged(ref _m08, value); }
    public int M09 { get { Counts.Read(9); return _m09; } set => this.RaiseAndSetIfChanged(ref _m09, value); }
    public string M10 { get { Counts.Read(10); return _m10; } set => this.RaiseAndSetIfChanged(ref _m10, value); }
    public int M11 { get { Counts.Read(11); return _m11; } set => this.RaiseAndSetIfChanged(ref _m11, value); }
    public string M12 { get { Counts.Read(12); return _m12; } set => this.RaiseAndSetIfChanged(ref _m12, value); }
    public int M13 { get { Counts.Read(13); return _m13; } set => this.RaiseAndSetIfChanged(ref _m13, value); }
    public string M14 { get { Counts.Read(14); return _m14; } set => this.RaiseAndSetIfChanged(ref _m14, value); }
    public int M15 { get { Counts.Read(15); return _m15; } set => this.RaiseAndSetIfChanged(ref _m15, value); }
    public string M16 { get { Counts.Read(16); return _m16; } set => this.RaiseAndSetIfChanged(ref _m16, value); }
    public int M17 { get { Counts.Read(17); return _m17; } set => this.RaiseAndSetIfChanged(ref _m17, value); }
    public string M18 { get { Counts.Read(18); return _m18; } set => this.RaiseAndSetIfChanged(ref _m18, value); }
    public int M19 { get { Counts.Read(19); return _m19; } set => this.RaiseAndSetIfChanged(ref _m19, value); }
    public string M20 { get { Counts.Read(20); return _m20; } set => this.RaiseAndSetIfChanged(ref _m20, value); }
    public int M21 { get { Counts.Read(21); return _m21; } set => this.RaiseAndSetIfChanged(ref _m21, value); }
    public string M22 { get { Counts.Read(22); return _m22; } set => this.RaiseAndSetIfChanged(ref _m22, value); }
    public int M23 { get { Counts.Read(23); return _m23; } set => this.RaiseAndSetIfChanged(ref _m23, value); }
    public string M24 { get { Counts.Read(24); return _m24; } set => this.RaiseAndSetIfChanged(ref _m24, value); }
    public int M25 { get { Counts.Read(25); return _m25; } set => this.RaiseAndSetIfChanged(ref _m25, value); }
    public string M26 { get { Counts.Read(26); return _m26; } set => this.RaiseAndSetIfChanged(ref _m26, value); }
    public int M27 { get { Counts.Read(27); return _m27; } set => this.RaiseAndSetIfChanged(ref _m27, value); }
    public string M28 { get { Counts.Read(28); return _m28; } set => this.RaiseAndSetIfChanged(ref _m28, value); }
    public int M29 { get { Counts.Read(29); return _m29; } set => this.RaiseAndSetIfChanged(ref _m29, value); }
    public string M30 { get { Counts.Read(30); return _m30; } set => this.RaiseAndSetIfChanged(ref _m30, value); }
    public int M31 { get { Counts.Read(31); return _m31; } set => this.RaiseAndSetIfChanged(ref _m31, value); }
    public string M32 { get { Counts.Read(32); return _m32; } set => this.RaiseAndSetIfChanged(ref _m32, value); }
    public int M33 { get { Counts.Read(33); return _m33; } set => this.RaiseAndSetIfChanged(ref _m33, value); }
    public string M34 { get { Counts.Read(34); return _m34; } set => this.RaiseAndSetIfChanged(ref _m34, value); }
    public int M35 { get { Counts.Read(35); return _m35; } set => this.RaiseAndSetIfChanged(ref _m35, value); }
    public string M36 { get { Counts.Read(36); return _m36; } set => this.RaiseAndSetIfChanged(ref _m36, value); }
    public int M37 { get { Counts.Read(37); return _m37; } set => this.RaiseAndSetIfChanged(ref _m37, value); }
    public string M38 { get { Counts.Read(38); return _m38; } set => this.RaiseAndSetIfChanged(ref _m38, value); }
    public int M39 { get { Counts.Read(39); return _m39; } set => this.RaiseAndSetIfChanged(ref _m39, value); }
    public string M40 { get { Counts.Read(40); return _m40; } set => this.RaiseAndSetIfChanged(ref _m40, value); }
    public int M41 { get { Counts.Read(41); return _m41; } set => this.RaiseAndSetIfChanged(ref _m41, value); }
    public string M42 { get { Counts.Read(42); return _m42; } set => this.RaiseAndSetIfChanged(ref _m42, value); }
    public int M43 { get { Counts.Read(43); return _m43; } set => this.RaiseAndSetIfChanged(ref _m43, value); }
    public string M44 { get { Counts.Read(44); return _m44; } set => this.RaiseAndSetIfChanged(ref _m44, value); }
    public int M45 { get { Counts.Read(45); return _m45; } set => this.RaiseAndSetIfChanged(ref _m45, value); }
    public string M46 { get { Counts.Read(46); return _m46; } set => this.RaiseAndSetIfChanged(ref _m46, value); }
    public int M47 { get { Counts.Read(47); return _m47; } set => this.RaiseAndSetIfChanged(ref _m47, value); }
    public string M48 { get { Counts.Read(48); return _m48; } set => this.RaiseAndSetIfChanged(ref _m48, value); }
    public int M49 { get { Counts.Read(49); return _m49; } set => this.RaiseAndSetIfChanged(ref _m49, value); }
    public string M50 { get { Counts.Read(50); return _m50; } set => this.RaiseAndSetIfChanged(ref _m50, value); }
    public int M51 { get { Counts.Read(51); return _m51; } set => this.RaiseAndSetIfChanged(ref _m51, value); }
    public string M52 { get { Counts.Read(52); return _m52; } set => this.RaiseAndSetIfChanged(ref _m52, value); }
    public int M53 { get { Counts.Read(53); return _m53; } set => this.RaiseAndSetIfChanged(ref _m53, value); }
    public string M54 { get { Counts.Read(54); return _m54; } set => this.RaiseAndSetIfChanged(ref _m54, value); }
    public int M55 { get { Counts.Read(55); return _m55; } set => this.RaiseAndSetIfChanged(ref _m55, value); }
    public string M56 { get { Counts.Read(56); return _m56; } set => this.RaiseAndSetIfChanged(ref _m56, value); }
    public int M57 { get { Counts.Read(57); return _m57; } set => this.RaiseAndSetIfChanged(ref _m57, value); }
    public string M58 { get { Counts.Read(58); return _m58; } set => this.RaiseAndSetIfChanged(ref _m58, value); }
    public int M59 { get { Counts.Read(59); return _m59; } set => this.RaiseAndSetIfChanged(ref _m59, value); }
    public string M60 { get { Counts.Read(60); return _m60; } set => this.RaiseAndSetIfChanged(ref _m60, value); }
    public int M61 { get { Counts.Read(61); return _m61; } set => this.RaiseAndSetIfChanged(ref _m61, value); }
    public string M62 { get { Counts.Read(62); return _m62; } set => this.RaiseAndSetIfChanged(ref _m62, value); }
    public int M63 { get { Counts.Read(63); return _m63; } set => this.RaiseAndSetIfChanged(ref _m63, value); }
    public string M64 { get { Counts.Read(64); return _m64; } set => this.RaiseAndSetIfChanged(ref _m64, value); }
    public int M65 { get { Counts.Read(65); return _m65; } set => this.RaiseAndSetIfChanged(ref _m65, value); }
    public string M66 { get { Counts.Read(66); return _m66; } set => this.RaiseAndSetIfChanged(ref _m66, value); }
    public int M67 { get { Counts.Read(67); return _m67; } set => this.RaiseAndSetIfChanged(ref _m67, value); }
    public string M68 { get { Counts.Read(68); return _m68; } set => this.RaiseAndSetIfChanged(ref _m68, value); }
    public int M69 { get { Counts.Read(69); return _m69; } set => this.RaiseAndSetIfChanged(ref _m69, value); }
    public string M70 { get { Counts.Read(70); return _m70; } set => this.RaiseAndSetIfChanged(ref _m70, value); }
    public int M71 { get { Counts.Read(71); return _m71; } set => this.RaiseAndSetIfChanged(ref _m71, value); }
    public string M72 { get { Counts.Read(72); return _m72; } set => this.RaiseAndSetIfChanged(ref _m72, value); }
    public int M73 { get { Counts.Read(73); return _m73; } set => this.RaiseAndSetIfChanged(ref _m73, value); }
    public string M74 { get { Counts.Read(74); return _m74; } set => this.RaiseAndSetIfChanged(ref _m74, value); }
    public int M75 { get { Counts.Read(75); return _m75; } set => this.RaiseAndSetIfChanged(ref _m75, value); }
    public string M76 { get { Counts.Read(76); return _m76; } set => this.RaiseAndSetIfChanged(ref _m76, value); }
    public int M77 { get { Counts.Read(77); return _m77; } set => this.RaiseAndSetIfChanged(ref _m77, value); }
    public string M78 { get { Counts.Read(78); return _m78; } set => this.RaiseAndSetIfChanged(ref _m78, value); }
    public int M79 { get { Counts.Read(79); return _m79; } set => this.RaiseAndSetIfChanged(ref _m79, value); }

    public void SetScalar(int index, int value)
    {
        switch (index)
        {
            case 0: M00 = value.ToString(CultureInfo.InvariantCulture); break;
            case 1: M01 = value; break;
            case 2: M02 = value.ToString(CultureInfo.InvariantCulture); break;
            case 3: M03 = value; break;
            case 4: M04 = value.ToString(CultureInfo.InvariantCulture); break;
            case 5: M05 = value; break;
            case 6: M06 = value.ToString(CultureInfo.InvariantCulture); break;
            case 7: M07 = value; break;
            case 8: M08 = value.ToString(CultureInfo.InvariantCulture); break;
            case 9: M09 = value; break;
            case 10: M10 = value.ToString(CultureInfo.InvariantCulture); break;
            case 11: M11 = value; break;
            case 12: M12 = value.ToString(CultureInfo.InvariantCulture); break;
            case 13: M13 = value; break;
            case 14: M14 = value.ToString(CultureInfo.InvariantCulture); break;
            case 15: M15 = value; break;
            case 16: M16 = value.ToString(CultureInfo.InvariantCulture); break;
            case 17: M17 = value; break;
            case 18: M18 = value.ToString(CultureInfo.InvariantCulture); break;
            case 19: M19 = value; break;
            case 20: M20 = value.ToString(CultureInfo.InvariantCulture); break;
            case 21: M21 = value; break;
            case 22: M22 = value.ToString(CultureInfo.InvariantCulture); break;
            case 23: M23 = value; break;
            case 24: M24 = value.ToString(CultureInfo.InvariantCulture); break;
            case 25: M25 = value; break;
            case 26: M26 = value.ToString(CultureInfo.InvariantCulture); break;
            case 27: M27 = value; break;
            case 28: M28 = value.ToString(CultureInfo.InvariantCulture); break;
            case 29: M29 = value; break;
            case 30: M30 = value.ToString(CultureInfo.InvariantCulture); break;
            case 31: M31 = value; break;
            case 32: M32 = value.ToString(CultureInfo.InvariantCulture); break;
            case 33: M33 = value; break;
            case 34: M34 = value.ToString(CultureInfo.InvariantCulture); break;
            case 35: M35 = value; break;
            case 36: M36 = value.ToString(CultureInfo.InvariantCulture); break;
            case 37: M37 = value; break;
            case 38: M38 = value.ToString(CultureInfo.InvariantCulture); break;
            case 39: M39 = value; break;
            case 40: M40 = value.ToString(CultureInfo.InvariantCulture); break;
            case 41: M41 = value; break;
            case 42: M42 = value.ToString(CultureInfo.InvariantCulture); break;
            case 43: M43 = value; break;
            case 44: M44 = value.ToString(CultureInfo.InvariantCulture); break;
            case 45: M45 = value; break;
            case 46: M46 = value.ToString(CultureInfo.InvariantCulture); break;
            case 47: M47 = value; break;
            case 48: M48 = value.ToString(CultureInfo.InvariantCulture); break;
            case 49: M49 = value; break;
            case 50: M50 = value.ToString(CultureInfo.InvariantCulture); break;
            case 51: M51 = value; break;
            case 52: M52 = value.ToString(CultureInfo.InvariantCulture); break;
            case 53: M53 = value; break;
            case 54: M54 = value.ToString(CultureInfo.InvariantCulture); break;
            case 55: M55 = value; break;
            case 56: M56 = value.ToString(CultureInfo.InvariantCulture); break;
            case 57: M57 = value; break;
            case 58: M58 = value.ToString(CultureInfo.InvariantCulture); break;
            case 59: M59 = value; break;
            case 60: M60 = value.ToString(CultureInfo.InvariantCulture); break;
            case 61: M61 = value; break;
            case 62: M62 = value.ToString(CultureInfo.InvariantCulture); break;
            case 63: M63 = value; break;
            case 64: M64 = value.ToString(CultureInfo.InvariantCulture); break;
            case 65: M65 = value; break;
            case 66: M66 = value.ToString(CultureInfo.InvariantCulture); break;
            case 67: M67 = value; break;
            case 68: M68 = value.ToString(CultureInfo.InvariantCulture); break;
            case 69: M69 = value; break;
            case 70: M70 = value.ToString(CultureInfo.InvariantCulture); break;
            case 71: M71 = value; break;
            case 72: M72 = value.ToString(CultureInfo.InvariantCulture); break;
            case 73: M73 = value; break;
            case 74: M74 = value.ToString(CultureInfo.InvariantCulture); break;
            case 75: M75 = value; break;
            case 76: M76 = value.ToString(CultureInfo.InvariantCulture); break;
            case 77: M77 = value; break;
            case 78: M78 = value.ToString(CultureInfo.InvariantCulture); break;
            case 79: M79 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, null);
        }
    }

    public void Notify(string? member) => this.RaisePropertyChanged(member ?? "");
}

public sealed partial class Wide80ReactiveWindow : RunicWindow<Wide80ReactiveViewModel>;

public sealed class Collections1ToolkitViewModel : ObservableObject, ICollectionsViewModel
{
    private readonly ObservableCollection<ScalingRow> _rows0 = [];
    private int _selected;

    // Rows0..Rows0, then Selected.
    [RunicIgnore] public GetterCounts Counts { get; } = new(2);
    [RunicIgnore] public int Size => 1;

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows0 { get { Counts.Read(0); return _rows0; } }

    public int Selected { get { Counts.Read(1); return _selected; } set => SetProperty(ref _selected, value); }

    public ObservableCollection<ScalingRow> Rows(int index) => index switch
    {
        0 => _rows0,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));
}

public sealed partial class Collections1ToolkitWindow : RunicWindow<Collections1ToolkitViewModel>;

public sealed class Collections1ReactiveViewModel : ReactiveObject, ICollectionsViewModel
{
    private readonly ObservableCollection<ScalingRow> _rows0 = [];
    private int _selected;

    // Rows0..Rows0, then Selected.
    [RunicIgnore] public GetterCounts Counts { get; } = new(2);
    [RunicIgnore] public int Size => 1;

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows0 { get { Counts.Read(0); return _rows0; } }

    public int Selected { get { Counts.Read(1); return _selected; } set => this.RaiseAndSetIfChanged(ref _selected, value); }

    public ObservableCollection<ScalingRow> Rows(int index) => index switch
    {
        0 => _rows0,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    public void Notify(string? member) => this.RaisePropertyChanged(member ?? "");
}

public sealed partial class Collections1ReactiveWindow : RunicWindow<Collections1ReactiveViewModel>;

public sealed class Collections4ToolkitViewModel : ObservableObject, ICollectionsViewModel
{
    private readonly ObservableCollection<ScalingRow> _rows0 = [];
    private readonly ObservableCollection<ScalingRow> _rows1 = [];
    private readonly ObservableCollection<ScalingRow> _rows2 = [];
    private readonly ObservableCollection<ScalingRow> _rows3 = [];
    private int _selected;

    // Rows0..Rows3, then Selected.
    [RunicIgnore] public GetterCounts Counts { get; } = new(5);
    [RunicIgnore] public int Size => 4;

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows0 { get { Counts.Read(0); return _rows0; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows1 { get { Counts.Read(1); return _rows1; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows2 { get { Counts.Read(2); return _rows2; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows3 { get { Counts.Read(3); return _rows3; } }

    public int Selected { get { Counts.Read(4); return _selected; } set => SetProperty(ref _selected, value); }

    public ObservableCollection<ScalingRow> Rows(int index) => index switch
    {
        0 => _rows0,
        1 => _rows1,
        2 => _rows2,
        3 => _rows3,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    public void Notify(string? member) => OnPropertyChanged(new PropertyChangedEventArgs(member));
}

public sealed partial class Collections4ToolkitWindow : RunicWindow<Collections4ToolkitViewModel>;

public sealed class Collections4ReactiveViewModel : ReactiveObject, ICollectionsViewModel
{
    private readonly ObservableCollection<ScalingRow> _rows0 = [];
    private readonly ObservableCollection<ScalingRow> _rows1 = [];
    private readonly ObservableCollection<ScalingRow> _rows2 = [];
    private readonly ObservableCollection<ScalingRow> _rows3 = [];
    private int _selected;

    // Rows0..Rows3, then Selected.
    [RunicIgnore] public GetterCounts Counts { get; } = new(5);
    [RunicIgnore] public int Size => 4;

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows0 { get { Counts.Read(0); return _rows0; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows1 { get { Counts.Read(1); return _rows1; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows2 { get { Counts.Read(2); return _rows2; } }

    [RunicCollection(nameof(ScalingRow.Key))]
    public ObservableCollection<ScalingRow> Rows3 { get { Counts.Read(3); return _rows3; } }

    public int Selected { get { Counts.Read(4); return _selected; } set => this.RaiseAndSetIfChanged(ref _selected, value); }

    public ObservableCollection<ScalingRow> Rows(int index) => index switch
    {
        0 => _rows0,
        1 => _rows1,
        2 => _rows2,
        3 => _rows3,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, null),
    };

    public void Notify(string? member) => this.RaisePropertyChanged(member ?? "");
}

public sealed partial class Collections4ReactiveWindow : RunicWindow<Collections4ReactiveViewModel>;
