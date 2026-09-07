using Silk.NET.Windowing;
using Silk.NET.Maths;
using Silk.NET.Input;
using System;
using System.Collections.Generic;
using System.Text;
using Kiln.Runtime.Threading;
using Kiln.Runtime.Input;

namespace Kiln.Runtime.Hosting
{
    public class KilnHost
    {
        private readonly IWindow _window;
        private ChannelManager _channelManager;
        private InputManager _inputManager;
        private RenderThread _renderThread;
        private SimulationThread _simulationThread;
        private CancellationTokenSource _cts;

        public KilnHost(IGame game)
        {
            var windowOptions = new WindowOptions
            {
                Title = "Kiln Engine",
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 5)),
                Size = new Vector2D<int>(1280,720)
            };
            _window = Window.Create(windowOptions);

            _inputManager = new(_window);
        }

    }
}
