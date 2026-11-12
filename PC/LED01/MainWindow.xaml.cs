using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Interop;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
namespace LED01
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        const byte STX = 0x02;
        const byte ETX = 0x03;
        const byte ACK = 0x06;
        const byte NAK = 0x15;
        const byte LED_WRITE = 0x01;
        const byte LED_RESP = 0x82;
        const byte CMD_CALC = 0x10;
        bool collecting = false;
        bool running = false;
        bool yellowOn = false;
        bool redOn = false;
        bool greenOn = false;
        int id = 0; 
        int logCount = 0;

        DateTime pressTime;

        List<byte> rxBuf = new List<byte>();

        CancellationTokenSource? cts;
        



        ConcurrentQueue<byte[]> pktQueue = new ConcurrentQueue<byte[]>();

        Random rnd = new Random();
        int count =0;
        int tickCount = 0;
        int a = 0;
        int op = 0;
        int b = 0;
        //Thread rxThread , procThread, statusThread;
        Task? rxTask, procTask, statusTask;
        enum LedColor : byte
        {
            wait = 1,work= 2, error = 3
        }
        LedColor state = LedColor.wait;
        private readonly object sendLock = new object();
        
        SerialPort serialPort = new SerialPort();
        public MainWindow()
        {
            InitializeComponent();
            Green.Fill = Brushes.Green;
            serialPort.PortName = "COM3";
            serialPort.BaudRate = 115200;
            serialPort.Open();
            StartThreads();
            StopButton.IsEnabled = false;
            StartButton.IsEnabled = true;
            if (!File.Exists("log.csv"))
                {
                    File.AppendAllText("log.csv", "ID,LogCount,Result" + Environment.NewLine, Encoding.UTF8);
                }     
        }
        byte CalcCrc(byte[] data, int start, int len)
        {
            byte crc = 0;
            for (int i = start; i < start + len; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x80) != 0)
                    {
                        crc = (byte)(crc << 1 ^ 0x07);
                    }
                    else
                    {
                        crc = (byte)(crc << 1);
                    }
                }

            }
            return crc;
        }
        void SendPacket(byte[] data, int cmd, int len)
        {
            byte[] pkt = new byte[5 + data.Length];

            lock (sendLock)
            {

                pkt[0] = STX;
                pkt[1] = (byte)cmd;
                pkt[2] = (byte)len;
                for (int i = 0; i < data.Length; i++)
                {
                    pkt[3 + i] = data[i];
                }
                pkt[3 + len] = CalcCrc(pkt, 1, 2 + len);
                pkt[4 + len] = ETX;
                serialPort.Write(pkt, 0, pkt.Length);
                AddLog("보냄", pkt);
            }
        }
        void StartThreads() {
            running = true;
            rxTask = Task.Run(ReceiveLoop);
            procTask = Task.Run(ProcessLoop);
        }
        private void ReceiveLoop() 
        {
            while (running) 
            {
                if (serialPort.BytesToRead ==0) {
                    Thread.Sleep(5);
                    continue;
                }
                byte b = (byte)serialPort.ReadByte();

                if (!collecting) {
                    if (b == STX) {
                        collecting = true;
                        rxBuf.Clear();
                        rxBuf.Add(b);
                    }
                    continue;
                }

                rxBuf.Add(b);

                if (rxBuf.Count >= 5 && rxBuf.Count == rxBuf[2] + 5)//len 데이터 길이 + STX, CMD, LEN, CRC, ETX
                {
                    byte[] pkt = rxBuf.ToArray();
                    pktQueue.Enqueue(pkt);
                    collecting = false;
                    rxBuf.Clear();
                } else if (rxBuf.Count >= 32) {
                    collecting = false;
                    rxBuf.Clear();
                }

                
            }
        
        }
         void ProcessLoop() {
            while (running || !pktQueue.IsEmpty) 
            {
                if (pktQueue.TryDequeue(out byte[]? pkt))
                {
                    int len = pkt[2];
                    byte etx = pkt[4 + len];
                    byte receivedCrc = pkt[3 + len];
                    byte myCrc = CalcCrc(pkt, 1, 2 + len);

                    bool endOk = etx == ETX;
                    bool crcOk = receivedCrc == myCrc;

                    if (endOk && crcOk) HandlePacket(pkt);
                } else Thread.Sleep(5);
            }
        }

    

        void AddLog(string dir, byte[] pkt)
        {
            lock (sendLock)
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff}  {dir}  {BitConverter.ToString(pkt)}";
                Dispatcher.BeginInvoke(() =>
                {
                    CommunicationLog.Items.Add(line);
                    if (CommunicationLog.Items.Count > 200) CommunicationLog.Items.RemoveAt(0);
                    CommunicationLog.ScrollIntoView(CommunicationLog.Items[CommunicationLog.Items.Count - 1]);
                });
            }
        }
        void HandlePacket(byte[] data)
        {

            AddLog("받음", data);

            byte len = data[2];
            switch (data[1])
            {
                case ACK:
                    break;
                case CMD_CALC:
                    int result = data[3] << 8 | data[4];
                    logCount++;
                    string oper = "";
                    if (op == 1)
                    {
                        oper = "+";
                    }
                    else if (op == 2)
                    {
                        oper = "-";
                    }
                    else if (op == 3)
                    {
                        oper = "*";
                    }
                    else if (op == 4)
                    {
                        oper = "/";
                    }
                    else if (op == 5)
                    {
                        oper = "%";
                    }
                    var row = new { A = a, B = b, Operation = oper, Result = result };

                    try
                    {
                        File.AppendAllText("log.csv", $"{id},{logCount},{result}" + Environment.NewLine, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                      
                        cts?.Cancel();
                        Dispatcher.BeginInvoke(() =>
                        {
                            CalculationHistory.Items.Clear();
                            CommunicationLog.Items.Clear();
                        });
                        MessageBox.Show($"파일을 닫아주세요.: {ex.Message}");
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        CalculationHistory.Items.Add(row);//여기에 한번에 다 추가하는게 아니라 row로 묶어서 사용해서 리팩터링 함
                        CalculationHistory.ScrollIntoView(row);//ScrollIntoView 최신줄 보이게하는 기능
                     
                    });
                    break;
                case LED_WRITE:
                    LedColor boardState = (LedColor)data[3];
                    Dispatcher.BeginInvoke(() =>
                    {
                        Yellow.Fill = Brushes.White;
                        Green.Fill = Brushes.White;
                        Red.Fill = Brushes.White;

                        if (boardState == LedColor.work)
                        {
                            Start_Click(null,null);
                        }
                        else if (boardState == LedColor.error)
                        {
                            Red.Fill = Brushes.Red;
                            cts?.Cancel();
                            
                        }else if(boardState == LedColor.wait)
                        {
                            Green.Fill = Brushes.Green;
                            state = LedColor.wait;
                            
                        }
                    });
                    break;
                case NAK:
                    break;
              
            }
        }

        private void workCount_TextChanged(object sender, TextChangedEventArgs e)
        {

        }

     

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (state != LedColor.work)  return;    // 작업 중일 때만
            cts?.Cancel();
            //StopButton.IsEnabled
            StopButton.IsEnabled = true;
            StartButton.IsEnabled = false;
        }
        private void Stop_Down(object sender, MouseButtonEventArgs e)
        {
            pressTime = DateTime.Now;// 누른 시각 저장
        }
        private void Stop_Up(object sender, MouseButtonEventArgs e)
        {
            double sec = (DateTime.Now - pressTime).TotalSeconds;   // 누른 시간(초) 누른시각과 현재 시각의 차이 계산
            if (sec >= 3 && state == LedColor.error)
            {
                state = LedColor.wait;
                Red.Fill = Brushes.White;
                Yellow.Fill = Brushes.White;
                Green.Fill = Brushes.Green;
                StopButton.IsEnabled = false;
                StartButton.IsEnabled = true;
                SendPacket(new[] { (byte)LedColor.wait }, LED_WRITE, 1);
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (state != LedColor.wait) return;
            if (!int.TryParse(WorkCount.Text, out count) || count <= 0) {
                SendPacket(new[] { (byte)LedColor.wait }, LED_WRITE, 1);
                MessageBox.Show("작업 횟수를 1 이상 입력해주세요");
                return;
            }
            Green.Fill = Brushes.White;
            SendPacket(new[] { (byte)LedColor.work }, LED_WRITE, 1);
            state = LedColor.work;
            cts = new CancellationTokenSource();
            WorkCount.IsReadOnly = true;
            CalculationHistory.Items.Clear();
            CommunicationLog.Items.Clear();
            id++;
            logCount = 0; 
            try
            {
               
                for (int i = 0; i < count; i++)
                {
                    StopButton.IsEnabled = true;
                    StartButton.IsEnabled = false;
                    a = rnd.Next(1, 101);
                    op = rnd.Next(1, 5);
                    b = rnd.Next(1, 101);
                    SendPacket(new byte[] { (byte)a, (byte)op, (byte)b }, CMD_CALC, 3);

                    Yellow.Fill = Brushes.Yellow;
                    await Task.Delay(500, cts.Token);
                    Yellow.Fill = Brushes.White;
                    await Task.Delay(500, cts.Token);
                    
                

                }
                StopButton.IsEnabled = false;
                StartButton.IsEnabled = true;
                state = LedColor.wait;
                SendPacket(new[] { (byte)LedColor.wait }, LED_WRITE, 1);
                Green.Fill = Brushes.Green;
                WorkCount.IsReadOnly = false;

            }
            catch (OperationCanceledException)
            {

                SendPacket(new[] { (byte)LedColor.error }, LED_WRITE, 1);
                state = LedColor.error;
                Red.Fill = Brushes.Red;
                Yellow.Fill = Brushes.White;
                Green.Fill = Brushes.White;
                StopButton.IsEnabled = true;
                StartButton.IsEnabled = false;
                WorkCount.IsReadOnly = false;

            }
        }
        

        private void communicationLog_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void calculationHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }
        private void WorkCount_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }
    }
}