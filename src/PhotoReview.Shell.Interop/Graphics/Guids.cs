namespace PhotoReview.Shell.Interop.Graphics;

/// <summary>
/// WP-13b: IID của các interface đồ hoạ. Hằng chuỗi để dùng trong <c>[Guid]</c>; <see cref="Iid"/> là dạng
/// <see cref="Guid"/> để truyền cho D2D1CreateFactory/DWriteCreateFactory/QueryInterface/GetBuffer.
/// Mọi IID ở đây đều được <c>D2DSmokeTests</c> chạy thật (QueryInterface/tạo factory) - sai một ký tự là test đỏ.
/// </summary>
internal static class GraphicsGuids
{
    // DXGI
    public const string DxgiObject = "aec22fb8-76f3-4639-9be0-28eb43a67a2e";
    public const string DxgiDeviceSubObject = "3d3e0379-f9de-4d58-bb6c-18d62992f1a6";
    public const string DxgiAdapter = "2411e7e1-12ac-4ccf-bd14-9798e8534dc0";
    public const string DxgiDevice = "54ec77fa-1377-44e6-8c32-88fd5f44c84c";
    public const string DxgiDevice1 = "77db970f-6276-48ba-ba28-070143b4392c";
    public const string DxgiSurface = "cafcb56c-6ac3-4889-bf47-9e23bbd260ec";
    public const string DxgiFactory = "7b7166ec-21c7-44ae-b21a-c9ae321ae369";
    public const string DxgiFactory1 = "770aae78-f26f-4dba-a829-253c83d1b387";
    public const string DxgiFactory2 = "50c83a1c-e072-4c48-87b0-3630fa36a6d0";
    public const string DxgiSwapChain = "310d36a0-d2e7-4c0a-aa04-6a9d23b8886a";
    public const string DxgiSwapChain1 = "790a45f7-0d42-4876-983a-0a55cfe6f4aa";
    public const string DxgiSwapChain2 = "a8be2ac4-199f-4946-b331-79599fb98de7";

    // D3D11
    public const string D3D11Device = "db6f6ddb-ac77-4e88-8253-819df9bbf140";

    // Direct2D
    public const string D2D1Factory = "06152247-6f50-465a-9245-118bfd3b6007";
    public const string D2D1Factory1 = "bb12d362-daee-4b9a-aa1d-14ba401cfa1f";
    public const string D2D1Resource = "2cd90691-12e2-11dc-9fed-001143a055f9";
    public const string D2D1Device = "47dd575d-ac05-4cdd-8049-9b02cd16f44c";
    public const string D2D1Image = "65019f75-8da2-497c-b32c-dfa34e48ede6";
    public const string D2D1Bitmap = "a2296057-ea42-4099-983b-539fb6505426";
    public const string D2D1Bitmap1 = "a898a84c-3873-4588-b08b-ebbf978df041";
    public const string D2D1Brush = "2cd906a8-12e2-11dc-9fed-001143a055f9";
    public const string D2D1SolidColorBrush = "2cd906a9-12e2-11dc-9fed-001143a055f9";
    public const string D2D1RenderTarget = "2cd90694-12e2-11dc-9fed-001143a055f9";
    public const string D2D1DeviceContext = "e8f7fe7a-191c-466d-ad95-975678bda998";

    // DirectWrite
    public const string DWriteFactory = "b859ee5a-d838-4b5b-a2e8-1adc7d93db48";
    public const string DWriteTextFormat = "9c906818-31d7-4fd3-a151-7c5e225db55a";
    public const string DWriteTextLayout = "53737037-6d14-410b-9bfe-0b182bb70961";

    public static readonly Guid IidDxgiDevice = new(DxgiDevice);
    public static readonly Guid IidDxgiSurface = new(DxgiSurface);
    public static readonly Guid IidDxgiFactory2 = new(DxgiFactory2);
    public static readonly Guid IidDxgiSwapChain2 = new(DxgiSwapChain2);
    public static readonly Guid IidD3D11Device = new(D3D11Device);
    public static readonly Guid IidD2D1Factory1 = new(D2D1Factory1);
    public static readonly Guid IidDWriteFactory = new(DWriteFactory);
}
