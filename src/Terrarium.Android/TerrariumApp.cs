using Android.App;

namespace Terrarium.Android;

/// <summary>
/// 只为了挂应用级属性。
///
/// 图标不在这里声明的话，aapt2 打出来的 badging 里 icon 是空的 ——
/// 装到手机上就是一个白方块，看着像装坏了。
/// </summary>
[Application(Label = "生态箱 Terrarium", Icon = "@mipmap/ic_launcher")]
public sealed class TerrariumApp : Application
{
}
