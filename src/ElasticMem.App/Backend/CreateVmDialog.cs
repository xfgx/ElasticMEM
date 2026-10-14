using System.Text.Json;
using ElasticMem.Core.Backend;
namespace ElasticMem.App.Backend;

internal sealed class CreateVmDialog:Form
{
    sealed record Network(string Id,string Text){public override string ToString()=>Text;}
    public VmCreationPlan? Plan {get;private set;}
    public CreateVmDialog(JsonElement info,string suggestedName,int minimum,int startup,int maximum,int buffer)
    {
        Text="Создать и запустить Windows VM · ElasticMem";Width=960;Height=600;StartPosition=FormStartPosition.CenterParent;
        var name=new TextBox{Text=suggestedName,Width=330};var path=new TextBox{Width=660,ReadOnly=true};
        var choose=new Button{Text="Выбрать VHDX / ISO…",AutoSize=true};
        choose.Click+=(_,_)=>{using var picker=new OpenFileDialog{Filter="Готовая Windows VM (*.vhdx)|*.vhdx|Установщик Windows (*.iso)|*.iso",CheckFileExists=true,Multiselect=false};if(picker.ShowDialog(this)==DialogResult.OK)path.Text=picker.FileName;};
        var network=new ComboBox{Width=620,DropDownStyle=ComboBoxStyle.DropDownList};network.Items.Add(new Network("","Без сети (изолированно)"));
        foreach(var s in info.GetProperty("switches").EnumerateArray())network.Items.Add(new Network(s.GetProperty("id").GetString()!,s.GetProperty("name").GetString()+" · "+s.GetProperty("type").GetString()));
        network.SelectedIndex=0;
        for(int i=1;i<network.Items.Count;i++)if(((Network)network.Items[i]!).Text.StartsWith("Default Switch · Internal",StringComparison.OrdinalIgnoreCase))network.SelectedIndex=i;
        var cpu=new NumericUpDown{Minimum=1,Maximum=Math.Max(1,Math.Min(16,info.GetProperty("cpu_count").GetInt32())),Value=1,Width=70};cpu.Value=Math.Min(2,cpu.Maximum);
        var disk=new NumericUpDown{Minimum=32,Maximum=512,Value=64,Width=80};
        var consent=new CheckBox{Text="Я доверяю этому образу Windows, имею право его использовать и понимаю, к какой сети подключаю VM",AutoSize=true};
        var create=new Button{Text="Создать и запустить VM автоматически",AutoSize=true};
        create.Click+=(_,_)=>{
            try
            {
                if(!consent.Checked)throw new ArgumentException("Подтвердите выбор доверенного образа и сети.");
                string kind=Path.GetExtension(path.Text).TrimStart('.').ToLowerInvariant();
                var plan=new VmCreationPlan(name.Text.Trim(),path.Text,new string('0',64),kind,((Network)network.SelectedItem!).Id,(int)cpu.Value,(int)disk.Value,minimum,startup,maximum,buffer,Guid.NewGuid().ToString("D"));plan.Validate();
                if(kind=="iso"&&MessageBox.Show(this,"ISO создаст VM и запустит установщик. Установка Windows, учётная запись и лицензирование потребуют вашего участия. Для автоматического запуска готовой ОС выберите подготовленный VHDX. Продолжить с ISO?","Это не unattended-установка",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
                Plan=plan;DialogResult=DialogResult.OK;Close();
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"Проверьте параметры",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        var stack=new VStack{Padding=new Padding(18)};
        stack.Add(Ui.Hint("Готовый VHDX → приватная независимая копия → Gen2 + Secure Boot/vTPM + CPU/RAM/сеть → запуск → привязка GUID. Исходник не изменяется. Только Hyper-V; это ещё НЕ Windows-VMM с paging через наш SYS."));
        stack.Add(Ui.Row(Ui.L("Новое имя VM"),name)).Add(Ui.Row(path,choose));
        stack.Add(Ui.Hint("VHDX: отключённый standalone диск установленной UEFI/Gen2 Windows. Не диск вашей рабочей VM, не differencing chain и не клон domain-joined машины. ISO: только запуск установщика, не готовая Windows."));
        stack.Add(Ui.Row(Ui.L("Существующий switch"),network));
        stack.Add(Ui.Row(Ui.L("vCPU"),cpu,Ui.L("Новый диск ISO, GiB"),disk));
        stack.Add(Ui.Hint($"RAM min/start/max: {minimum}/{startup}/{maximum} MiB, buffer {buffer}%. Копия и конфигурация: ProgramData\\ElasticMemBackendVMs. Нужны место под полную ёмкость диска (+ ISO, если выбран) +4GiB и свободная RAM startup +1GiB. Копирование и SHA256 большого образа могут занять длительное время."));
        stack.Add(consent).Add(Ui.Hint("Существующие VM/свитчи не перезаписываются. Guest/RDP-пароли не переносятся. RDS/домен/лицензии, приложения и SYS автоматически не устанавливаются. При ошибке сохраняется журнал, без принудительного выключения/удаления."));
        stack.Add(Ui.Row(create));Controls.Add(stack);
    }
}
