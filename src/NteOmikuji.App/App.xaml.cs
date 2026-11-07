namespace NteOmikuji.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "NTE Omikuji", Width = 1180, Height = 800 };
	}
}
