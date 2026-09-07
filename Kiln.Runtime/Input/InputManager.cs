using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kiln.Runtime.Input
{
	public class InputManager
	{
		private readonly IWindow _window;
		public InputManager(IWindow window) {
			_window = window;

			_window.Load += OnWindowLoad;
			_window.Update += OnWindowUpdate;
		}

		private void OnWindowLoad()
		{
			//Create and persist InputContext
		}

		private void OnWindowUpdate(double deltaTime)
		{
			//Update InputManager.Keyboard and InputManager.Mouse with events, etc.
		}
	}
}
